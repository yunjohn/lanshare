using System.Text.Json;
using LanTransfer.Common.Constants;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Core.Transfers;

/// <summary>
/// 分块管理与断点续传元数据。写入使用随机访问（Seek + Write），
/// 因此即使块乱序到达或断线后重传也不会破坏已写入内容。
/// </summary>
public sealed class ChunkManager : IChunkManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly ILogger<ChunkManager> _logger;

    public ChunkManager(ILogger<ChunkManager> logger) => _logger = logger;

    public int CalculateTotalChunks(long fileSize, int chunkSize)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (fileSize <= 0) return 1;
        return (int)((fileSize + chunkSize - 1) / chunkSize);
    }

    public (long Offset, int Length) GetChunkRange(long fileSize, int chunkSize, int chunkIndex)
    {
        if (chunkIndex < 0) throw new ArgumentOutOfRangeException(nameof(chunkIndex));

        var offset = (long)chunkIndex * chunkSize;
        if (offset >= fileSize) return (offset, 0);

        var remaining = fileSize - offset;
        return (offset, (int)Math.Min(chunkSize, remaining));
    }

    public async Task WriteChunkAsync(string partFilePath, long offset, Stream source, int length,
        CancellationToken cancellationToken = default)
    {
        EnsureDirectory(partFilePath);

        await using var target = new FileStream(
            partFilePath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            AppConstants.StreamBufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        target.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[Math.Min(AppConstants.StreamBufferSize, Math.Max(length, 1))];
        var remaining = length;

        while (remaining > 0)
        {
            var want = Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                throw new IOException($"Chunk 数据提前结束，期望 {length} 字节，仍缺 {remaining} 字节。");

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ReadChunkAsync(string sourceFilePath, long offset, int length, Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await using var source = new FileStream(
            sourceFilePath,
            FileMode.Open,
            FileAccess.Read,
            AppConstants.FileReadSharing,
            AppConstants.StreamBufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        source.Seek(offset, SeekOrigin.Begin);

        var total = 0;
        while (total < length)
        {
            var read = await source.ReadAsync(buffer.Slice(total, length - total), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
        }

        return total;
    }

    public async Task<PartMetadata?> ReadMetadataAsync(string partFilePath,
        CancellationToken cancellationToken = default)
    {
        var metaPath = partFilePath + ".json";
        if (!File.Exists(metaPath)) return null;

        try
        {
            await using var stream = File.OpenRead(metaPath);
            return await JsonSerializer.DeserializeAsync<PartMetadata>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取续传元数据失败: {Path}", metaPath);
            return null;
        }
    }

    public async Task WriteMetadataAsync(string partFilePath, PartMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        var metaPath = partFilePath + ".json";
        EnsureDirectory(metaPath);
        metadata.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            var tempPath = metaPath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(tempPath, metaPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入续传元数据失败: {Path}", metaPath);
        }
    }

    public Task DeletePartFilesAsync(string partFilePath, CancellationToken cancellationToken = default)
    {
        TryDelete(partFilePath);
        TryDelete(partFilePath + ".json");
        TryDelete(partFilePath + ".tmp");
        return Task.CompletedTask;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除文件失败: {Path}", path);
        }
    }

    private static void EnsureDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }
}
