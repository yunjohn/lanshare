using System.Security.Cryptography;
using System.Text;
using LanTransfer.Core.Files;
using LanTransfer.Core.Hashing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LanTransfer.Core.Tests;

/// <summary>流式 SHA-256 校验与文件扫描。</summary>
public class HashServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly HashService _hash = new(NullLogger<HashService>.Instance);

    public HashServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lantransfer-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 忽略 */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ComputeHash_MatchesKnownSha256Vector()
    {
        // 已知测试向量：SHA-256("abc")
        const string expected = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

        Assert.Equal(expected, _hash.ComputeHash(Encoding.UTF8.GetBytes("abc")));
    }

    [Fact]
    public async Task ComputeFileHashAsync_MatchesFrameworkHash()
    {
        var payload = new byte[5 * 1024 * 1024 + 123];
        new Random(1).NextBytes(payload);
        var file = Path.Combine(_dir, "big.bin");
        await File.WriteAllBytesAsync(file, payload);

        var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var actual = await _hash.ComputeFileHashAsync(file);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ComputeFileHashAsync_HandlesEmptyFile()
    {
        var file = Path.Combine(_dir, "empty.bin");
        await File.WriteAllBytesAsync(file, Array.Empty<byte>());

        var expected = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();

        Assert.Equal(expected, await _hash.ComputeFileHashAsync(file));
    }

    [Fact]
    public async Task VerifyFileHashAsync_IsCaseInsensitive()
    {
        var file = Path.Combine(_dir, "verify.txt");
        await File.WriteAllTextAsync(file, "hello lan transfer");

        var upper = (await _hash.ComputeFileHashAsync(file)).ToUpperInvariant();

        Assert.True(await _hash.VerifyFileHashAsync(file, upper));
        Assert.True(await _hash.VerifyFileHashAsync(file, upper.ToLowerInvariant()));
    }

    [Fact]
    public async Task VerifyFileHashAsync_ReturnsFalseOnMismatch()
    {
        var file = Path.Combine(_dir, "mismatch.txt");
        await File.WriteAllTextAsync(file, "content");

        Assert.False(await _hash.VerifyFileHashAsync(file, new string('0', 64)));
        Assert.False(await _hash.VerifyFileHashAsync(file, string.Empty));
    }

    [Fact]
    public async Task ComputeFileHashAsync_ReportsProgressMonotonically()
    {
        var payload = new byte[1024 * 1024];
        var file = Path.Combine(_dir, "progress.bin");
        await File.WriteAllBytesAsync(file, payload);

        var reports = new List<long>();

        // 不要用 Progress<T>：它把回调 Post 到捕获的 SynchronizationContext，
        // 断言可能先于最后一次上报执行（整套测试并发跑时必现），
        // 会表现为「reports.Last() < payload.Length」的随机失败。
        await _hash.ComputeFileHashAsync(file, new SynchronousProgress(reports));

        Assert.True(reports.Count > 0);
        Assert.True(reports.Last() >= payload.Length);
    }

    /// <summary>同步收集进度，消除 <see cref="Progress{T}"/> 异步回调带来的竞态。</summary>
    private sealed class SynchronousProgress(List<long> sink) : IProgress<long>
    {
        public void Report(long value)
        {
            lock (sink) sink.Add(value);
        }
    }

    [Fact]
    public async Task ComputeStreamHashAsync_DoesNotCloseCallerStream()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("abc"));

        var result = await _hash.ComputeStreamHashAsync(stream);

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", result);
        Assert.True(stream.CanRead);
    }
}

/// <summary>发送清单扫描：保留相对路径、跳过重解析点。</summary>
public class FileScannerTests : IDisposable
{
    private readonly string _dir;
    private readonly FileScanner _scanner = new(NullLogger<FileScanner>.Instance);

    public FileScannerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lantransfer-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 忽略 */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ScanFolder_PreservesRelativeStructure()
    {
        // 发送文件夹时，相对路径 = 根文件夹名 + 子目录，接收端会与 FileName 组合成最终路径
        var root = Path.Combine(_dir, "scanroot");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "root.txt"), "root");
        File.WriteAllText(Path.Combine(root, "sub", "child.txt"), "child");

        var entries = _scanner.ScanFolder(root);

        Assert.Equal(2, entries.Count);

        var rootFile = entries.Single(e => e.FileName == "root.txt");
        Assert.Equal("scanroot", rootFile.RelativePath);

        var childFile = entries.Single(e => e.FileName == "child.txt");
        Assert.Equal("scanroot\\sub", childFile.RelativePath);
    }

    [Fact]
    public void ScanFile_ReturnsNameSizeAndTimestamp()
    {
        var file = Path.Combine(_dir, "single.txt");
        File.WriteAllText(file, "abc");

        var descriptor = _scanner.ScanFile(file);

        Assert.Equal("single.txt", descriptor.FileName);
        // 单个文件的相对路径为空，接收端直接落在接收根目录
        Assert.Equal(string.Empty, descriptor.RelativePath);
        Assert.Equal(3, descriptor.FileSize);
        Assert.True(descriptor.LastWriteTimeUtc > DateTimeOffset.MinValue);
    }

    [Fact]
    public void ScanSelection_CombinesFilesAndFolders()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "folder"));
        File.WriteAllText(Path.Combine(_dir, "folder", "a.txt"), "a");
        File.WriteAllText(Path.Combine(_dir, "loose.txt"), "b");

        var entries = _scanner.ScanSelection(new[]
        {
            Path.Combine(_dir, "folder"),
            Path.Combine(_dir, "loose.txt"),
        });

        Assert.Equal(2, entries.Count);

        var fromFolder = entries.Single(e => e.FileName == "a.txt");
        Assert.Equal("folder", fromFolder.RelativePath);
        Assert.True(File.Exists(fromFolder.SourcePath));

        var loose = entries.Single(e => e.FileName == "loose.txt");
        Assert.Equal(string.Empty, loose.RelativePath);
    }

    [Fact]
    public void ScanSelection_IgnoresMissingPaths()
    {
        var entries = _scanner.ScanSelection(new[] { Path.Combine(_dir, "not-exists.txt") });

        Assert.Empty(entries);
    }

    [Fact]
    public void ScanFolder_EmptyDirectory_ReturnsEmpty()
    {
        Assert.Empty(_scanner.ScanFolder(_dir));
    }
}
