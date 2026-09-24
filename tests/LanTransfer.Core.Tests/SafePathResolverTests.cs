using LanTransfer.Common.Models;
using LanTransfer.Core.Files;
using Xunit;

namespace LanTransfer.Core.Tests;

/// <summary>
/// 路径穿越与重名策略测试。这是安全红线：远程提供的相对路径不得逃逸出 DownloadRoot。
/// </summary>
public class SafePathResolverTests : IDisposable
{
    private readonly string _root;
    private readonly SafePathResolver _resolver = new();

    public SafePathResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lantransfer-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("docs/a.txt")]
    [InlineData("docs\\sub\\b.bin")]
    [InlineData("./docs/./c.txt")]
    public void TryResolve_AcceptsNormalizedRelativePaths(string relativePath)
    {
        Assert.True(_resolver.TryResolve(_root, relativePath, out var fullPath));
        Assert.StartsWith(Path.GetFullPath(_root), fullPath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("docs/../../escape.txt")]
    [InlineData("..\\..\\escape.txt")]
    [InlineData("C:\\Windows\\System32\\evil.dll")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\evil.txt")]
    [InlineData("docs/file.txt:stream")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolve_RejectsTraversalAndAbsolutePaths(string relativePath)
    {
        Assert.False(_resolver.TryResolve(_root, relativePath, out var fullPath));
        Assert.Equal(string.Empty, fullPath);
    }

    [Fact]
    public void TryResolve_RejectsReservedDeviceNames()
    {
        Assert.False(_resolver.TryResolve(_root, "CON", out _));
        Assert.False(_resolver.TryResolve(_root, "com1.txt", out _));
        Assert.False(_resolver.TryResolve(_root, "LPT9.log", out _));
    }

    /// <summary>
    /// 回归测试：中间扩展名不能绕过保留设备名校验。
    /// "NUL.tar.gz" 在 Windows 上依然等价于 NUL 设备（写入被丢弃、文件不存在），
    /// "COM1.foo.txt" 会去打开串口 —— 只比较最后一个扩展名会全部放行。
    /// </summary>
    [Theory]
    [InlineData("NUL.tar.gz")]
    [InlineData("COM1.foo.txt")]
    [InlineData("lpt1.a.b.c")]
    [InlineData("aux. ")]
    public void TryResolve_RejectsDeviceNamesWithExtraExtensions(string name)
    {
        Assert.False(_resolver.IsValidFileName(name, out var reason));
        Assert.NotNull(reason);
        Assert.False(_resolver.TryResolve(_root, name, out _));
    }

    [Fact]
    public void TryResolve_RejectsTrailingDotOrSpace()
    {
        Assert.False(_resolver.TryResolve(_root, "name.", out _));
        Assert.False(_resolver.TryResolve(_root, "name ", out _));
    }

    [Fact]
    public void TryResolve_RejectsEmptyRoot()
    {
        Assert.False(_resolver.TryResolve(string.Empty, "a.txt", out _));
    }

    [Theory]
    [InlineData("docs/./a.txt", "docs\\a.txt")]
    [InlineData("./a.txt", "a.txt")]
    [InlineData("/a/b.txt", "a\\b.txt")]
    [InlineData("C:/a/b.txt", "a\\b.txt")]
    [InlineData("a\\b\\..\\c.txt", "a\\b\\..\\c.txt")]
    public void NormalizeRelativePath_ProducesCanonicalForm(string input, string expected)
    {
        Assert.Equal(expected, _resolver.NormalizeRelativePath(input));
    }

    [Theory]
    [InlineData("a.txt", true)]
    [InlineData("正常文件.docx", true)]
    [InlineData("a<b.txt", false)]
    [InlineData("a|b.txt", false)]
    [InlineData("a?b.txt", false)]
    [InlineData("NUL", false)]
    public void IsValidFileName_DetectsInvalidNames(string name, bool expected)
    {
        Assert.Equal(expected, _resolver.IsValidFileName(name, out _));
    }

    [Fact]
    public void IsValidFileName_RejectsOverlongName()
    {
        Assert.False(_resolver.IsValidFileName(new string('a', 256) + ".txt", out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void ResolveConflictName_ReturnsSamePathWhenNotExists()
    {
        var target = Path.Combine(_root, "report.docx");

        Assert.Equal(target, _resolver.ResolveConflictName(target, ConflictPolicy.Rename));
    }

    [Fact]
    public void ResolveConflictName_AppendsIncrementingIndex()
    {
        var target = Path.Combine(_root, "report.docx");
        File.WriteAllText(target, "v1");

        var first = _resolver.ResolveConflictName(target, ConflictPolicy.Rename);
        Assert.Equal(Path.Combine(_root, "report (1).docx"), first);

        File.WriteAllText(first, "v2");
        var second = _resolver.ResolveConflictName(target, ConflictPolicy.Rename);
        Assert.Equal(Path.Combine(_root, "report (2).docx"), second);
    }

    [Fact]
    public void ResolveConflictName_OverwriteAndSkipReturnOriginalPath()
    {
        var target = Path.Combine(_root, "exists.bin");
        File.WriteAllText(target, "x");

        Assert.Equal(target, _resolver.ResolveConflictName(target, ConflictPolicy.Overwrite));
        Assert.Equal(target, _resolver.ResolveConflictName(target, ConflictPolicy.Skip));
    }

    [Fact]
    public void GetAvailableFreeSpace_ReturnsPositiveForExistingDrive()
    {
        Assert.True(_resolver.GetAvailableFreeSpace(_root) > 0);
    }
}
