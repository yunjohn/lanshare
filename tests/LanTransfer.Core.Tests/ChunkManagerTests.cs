using LanTransfer.Core.Interfaces;
using LanTransfer.Core.Transfers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LanTransfer.Core.Tests;

/// <summary>分块计算、乱序写入、断点续传元数据。</summary>
public class ChunkManagerTests : IDisposable
{
    private const int MiB = 1024 * 1024;
    private readonly string _dir;
    private readonly ChunkManager _manager = new(NullLogger<ChunkManager>.Instance);

    public ChunkManagerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lantransfer-chunk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 忽略清理失败 */ }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(0L, 4 * MiB, 1)]
    [InlineData(1L, 4 * MiB, 1)]
    [InlineData(4L * MiB, 4 * MiB, 1)]
    [InlineData(4L * MiB + 1, 4 * MiB, 2)]
    [InlineData(10L * MiB, 4 * MiB, 3)]
    [InlineData(100L * 1024 * MiB, 4 * MiB, 25600)]
    public void CalculateTotalChunks_ReturnsExpectedCount(long fileSize, int chunkSize, int expected)
    {
        Assert.Equal(expected, _manager.CalculateTotalChunks(fileSize, chunkSize));
    }

    [Fact]
    public void CalculateTotalChunks_RejectsInvalidChunkSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _manager.CalculateTotalChunks(1024, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _manager.CalculateTotalChunks(1024, -1));
    }

    [Theory]
    [InlineData(100L, 30, 0, 0L, 30)]
    [InlineData(100L, 30, 1, 30L, 30)]
    [InlineData(100L, 30, 3, 90L, 10)]
    [InlineData(100L, 30, 4, 120L, 0)]
    public void GetChunkRange_ReturnsOffsetAndLength(long size, int chunk, int index, long offset, int length)
    {
        var (actualOffset, actualLength) = _manager.GetChunkRange(size, chunk, index);

        Assert.Equal(offset, actualOffset);
        Assert.Equal(length, actualLength);
    }

    [Fact]
    public void GetChunkRange_RejectsNegativeIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _manager.GetChunkRange(100, 30, -1));
    }

    [Fact]
    public async Task WriteChunkAsync_OutOfOrder_ProducesIdenticalFile()
    {
        var payload = new byte[3000];
        new Random(20260924).NextBytes(payload);

        const int chunkSize = 1024;
        var partPath = Path.Combine(_dir, "payload.bin.part");

        Assert.Equal(3, _manager.CalculateTotalChunks(payload.Length, chunkSize));

        // 故意乱序写入：2 → 0 → 1，验证随机写入不会破坏已写入的块
        foreach (var index in new[] { 2, 0, 1 })
        {
            var (offset, length) = _manager.GetChunkRange(payload.Length, chunkSize, index);
            using var source = new MemoryStream(payload, (int)offset, length);

            await _manager.WriteChunkAsync(partPath, offset, source, length);
        }

        var actual = await File.ReadAllBytesAsync(partPath);
        Assert.Equal(payload, actual);
    }

    [Fact]
    public async Task WriteChunkAsync_Retransmit_OverwritesSameRegion()
    {
        var payload = new byte[512];
        new Random(7).NextBytes(payload);

        var partPath = Path.Combine(_dir, "retransmit.bin.part");

        using (var first = new MemoryStream(payload))
            await _manager.WriteChunkAsync(partPath, 0, first, payload.Length);

        using (var second = new MemoryStream(payload))
            await _manager.WriteChunkAsync(partPath, 0, second, payload.Length);

        Assert.Equal(payload, await File.ReadAllBytesAsync(partPath));
    }

    [Fact]
    public async Task WriteChunkAsync_ThrowsWhenSourceIsShort()
    {
        var partPath = Path.Combine(_dir, "short.bin.part");
        using var source = new MemoryStream(new byte[10]);

        await Assert.ThrowsAsync<IOException>(() =>
            _manager.WriteChunkAsync(partPath, 0, source, 100));
    }

    [Fact]
    public async Task ReadChunkAsync_ReadsRequestedSlice()
    {
        var payload = new byte[2500];
        new Random(99).NextBytes(payload);
        var file = Path.Combine(_dir, "source.bin");
        await File.WriteAllBytesAsync(file, payload);

        var buffer = new byte[1000];
        var read = await _manager.ReadChunkAsync(file, 1000, 1000, buffer);

        Assert.Equal(1000, read);
        Assert.Equal(payload.Skip(1000).Take(1000).ToArray(), buffer);
    }

    [Fact]
    public async Task Metadata_RoundTrips_AndIsDeleted()
    {
        var partPath = Path.Combine(_dir, "meta.bin.part");
        await File.WriteAllBytesAsync(partPath, new byte[10]);

        var metadata = new PartMetadata
        {
            TransferId = "t-1",
            FileId = "f-1",
            FileName = "meta.bin",
            RelativePath = "docs/meta.bin",
            FileSize = 10,
            ChunkSize = 4 * MiB,
            TotalChunks = 1,
            Sha256 = "abc",
            RemoteDeviceId = "dev-1",
        };
        metadata.CompletedChunks.Add(0);

        await _manager.WriteMetadataAsync(partPath, metadata);

        var loaded = await _manager.ReadMetadataAsync(partPath);

        Assert.NotNull(loaded);
        Assert.Equal("t-1", loaded!.TransferId);
        Assert.Equal("docs/meta.bin", loaded.RelativePath);
        Assert.Equal(new[] { 0 }, loaded.CompletedChunks);
        Assert.True(loaded.UpdatedAt >= loaded.CreatedAt);

        await _manager.DeletePartFilesAsync(partPath);

        Assert.False(File.Exists(partPath));
        Assert.False(File.Exists(partPath + ".json"));
        Assert.Null(await _manager.ReadMetadataAsync(partPath));
    }

    [Fact]
    public async Task ReadMetadataAsync_ReturnsNullWhenMissing()
    {
        Assert.Null(await _manager.ReadMetadataAsync(Path.Combine(_dir, "nope.part")));
    }

    [Fact]
    public async Task Metadata_AtomicWrite_LeavesNoTempFile()
    {
        var partPath = Path.Combine(_dir, "atomic.bin.part");
        await _manager.WriteMetadataAsync(partPath, new PartMetadata { TransferId = "t" });

        Assert.True(File.Exists(partPath + ".json"));
        Assert.False(File.Exists(partPath + ".json.tmp"));
    }
}
