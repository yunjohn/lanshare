using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;

namespace LanTransfer.Core.Files;

/// <summary>
/// 路径安全解析器。所有由远程提供的相对路径在落盘前必须经过 <see cref="TryResolve"/>。
/// 目标：远程发送端不可能通过 ../ 、绝对路径、UNC、ADS 等方式逃逸出 DownloadRoot。
/// </summary>
public sealed class SafePathResolver : ISafePathResolver
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public bool TryResolve(string downloadRoot, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(downloadRoot) || string.IsNullOrWhiteSpace(relativePath))
            return false;

        // 首尾空白会被 Windows 静默裁剪，导致实际写入路径与预期不一致 → 直接拒绝
        if (!string.Equals(relativePath, relativePath.Trim(), StringComparison.Ordinal)) return false;

        // 绝对路径 / UNC / 盘符 / ADS 必须在规范化之前就拒绝。
        // 否则 "C:\a\b.txt" 会被 NormalizeRelativePath 去掉盘符而变成看似合法的相对路径。
        if (relativePath.Contains(':')) return false;
        if (Path.IsPathRooted(relativePath)) return false;

        string root;
        try
        {
            root = Path.GetFullPath(downloadRoot);
        }
        catch
        {
            return false;
        }

        var normalized = NormalizeRelativePath(relativePath);
        if (normalized.Length == 0) return false;

        // 二次确认（防止规范化过程重新引入绝对路径）
        if (Path.IsPathRooted(normalized)) return false;
        if (normalized.Contains(':')) return false;

        var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;

        foreach (var segment in segments)
        {
            // 关键：任何 ".." 段直接拒绝，绝不静默丢弃后继续写入
            if (segment == "..") return false;
            if (segment == ".") return false;
            if (!IsValidFileName(segment, out _)) return false;
        }

        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(root, string.Join('\\', segments)));
        }
        catch
        {
            return false;
        }

        // 二次确认：结果必须位于 root 之下
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        fullPath = combined;
        return true;
    }

    public string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return string.Empty;

        var value = relativePath.Replace('/', '\\').Trim();

        // 去掉盘符（C:）
        if (value.Length >= 2 && value[1] == ':' && char.IsLetter(value[0]))
            value = value[2..];

        // 去掉前导分隔符
        value = value.TrimStart('\\');

        // 折叠 "." 段；保留 ".." 段交由 TryResolve 拒绝
        var segments = value.Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != ".")
            .ToArray();

        return string.Join('\\', segments);
    }

    public bool IsValidFileName(string fileName, out string? reason)
    {
        reason = null;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            reason = "文件名为空";
            return false;
        }

        if (fileName.Length > 255)
        {
            reason = "文件名超过 255 个字符";
            return false;
        }

        if (fileName.IndexOfAny(InvalidFileNameChars) >= 0)
        {
            reason = "文件名包含非法字符";
            return false;
        }

        // 结尾的点与空格在 Windows 上会被静默裁剪，导致实际写入路径与预期不一致
        if (fileName.EndsWith('.') || fileName.EndsWith(' '))
        {
            reason = "文件名不能以点或空格结尾";
            return false;
        }

        // 保留设备名：必须比较「第一个点之前」的部分。
        // 用 Path.GetFileNameWithoutExtension 只能拦 NUL.txt，拦不住 NUL.tar.gz / COM1.foo.txt ——
        // 这类名字在 Windows 上依然等价于设备（写 NUL 会被丢弃、COM1 会去开串口），
        // 会导致接收端「写入成功但文件不存在」或报奇怪的 IO 错误。
        var stem = fileName.Split('.')[0].TrimEnd(' ', '.');
        if (ReservedNames.Contains(stem))
        {
            reason = $"\"{stem}\" 是 Windows 保留设备名";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 按重名策略解析最终落盘路径。
    /// 注意契约：<see cref="ConflictPolicy.Overwrite"/> 与 <see cref="ConflictPolicy.Skip"/>
    /// **都返回原路径**——「跳过」的语义（不接收该文件）必须由调用方先判定目标是否存在，
    /// 不能只靠本方法（否则会退化成覆盖）。
    /// </summary>
    public string ResolveConflictName(string targetPath, ConflictPolicy policy, string? deviceTag = null)
    {
        if (policy == ConflictPolicy.Overwrite) return targetPath;
        if (policy == ConflictPolicy.Skip) return targetPath;
        if (!File.Exists(targetPath) && !Directory.Exists(targetPath)) return targetPath;

        var directory = Path.GetDirectoryName(targetPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(targetPath);
        var extension = Path.GetExtension(targetPath);

        for (var index = 1; index < 100_000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }

        // 极端情况下回退到带时间戳与设备标识的名字
        var tag = string.IsNullOrEmpty(deviceTag) ? "copy" : deviceTag;
        return Path.Combine(directory,
            $"{name} ({tag}-{DateTime.Now:yyyyMMdd-HHmmss}){extension}");
    }

    public long GetAvailableFreeSpace(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return -1;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : -1;
        }
        catch
        {
            return -1;
        }
    }
}
