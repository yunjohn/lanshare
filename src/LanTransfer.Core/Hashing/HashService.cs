using System.Buffers;
using System.Security.Cryptography;
using LanTransfer.Common.Constants;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Core.Hashing;

/// <summary>
/// 流式 SHA-256。全程使用固定大小缓冲区，内存占用与文件大小无关。
/// </summary>
public sealed class HashService : IHashService
{
    private readonly ILogger<HashService> _logger;

    public HashService(ILogger<HashService> logger) => _logger = logger;

    public async Task<string> ComputeFileHashAsync(string filePath, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            AppConstants.FileReadSharing,
            AppConstants.StreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await ComputeStreamHashAsync(stream, progress, cancellationToken).ConfigureAwait(false);
    }

    public Task<string> ComputeStreamHashAsync(Stream stream, CancellationToken cancellationToken = default)
        => ComputeStreamHashAsync(stream, null, cancellationToken);

    public async Task<string> ComputeStreamHashAsync(Stream stream, IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(AppConstants.StreamBufferSize);

        try
        {
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, AppConstants.StreamBufferSize),
                       cancellationToken).ConfigureAwait(false)) > 0)
            {
                incremental.AppendData(buffer, 0, read);
                total += read;
                progress?.Report(total);
            }

            return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public string ComputeHash(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public async Task<bool> VerifyFileHashAsync(string filePath, string expectedSha256,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return false;

        var actual = await ComputeFileHashAsync(filePath, progress, cancellationToken).ConfigureAwait(false);
        var matches = string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);

        if (!matches)
        {
            // 只记录哈希与路径，绝不记录文件内容
            _logger.LogError("SHA-256 校验失败: {Path} expected={Expected} actual={Actual}",
                filePath, expectedSha256, actual);
        }

        return matches;
    }
}
