using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Core.Files;

/// <summary>
/// 发送端文件扫描。文件夹保留目录结构，通过 RelativePath 表达；
/// 跳过重解析点（符号链接 / 联接点）以避免环路与越界读取。
/// </summary>
public sealed class FileScanner : IFileScanner
{
    private readonly ILogger<FileScanner> _logger;

    public FileScanner(ILogger<FileScanner> logger) => _logger = logger;

    public TransferFileDescriptor ScanFile(string filePath)
    {
        var info = new FileInfo(filePath);
        return new TransferFileDescriptor
        {
            RelativePath = string.Empty,
            FileName = info.Name,
            FileSize = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
        };
    }

    public IReadOnlyList<TransferFileDescriptor> ScanFolder(string folderPath)
    {
        var result = new List<TransferFileDescriptor>();
        var rootName = new DirectoryInfo(folderPath).Name;

        foreach (var entry in EnumerateFolder(folderPath))
        {
            var relativeDirectory = Path.GetRelativePath(folderPath, Path.GetDirectoryName(entry.SourcePath)!);
            if (relativeDirectory == ".") relativeDirectory = string.Empty;

            var relativePath = string.IsNullOrEmpty(relativeDirectory)
                ? rootName
                : Path.Combine(rootName, relativeDirectory);

            result.Add(new TransferFileDescriptor
            {
                RelativePath = relativePath,
                FileName = entry.FileName,
                FileSize = entry.FileSize,
                LastWriteTimeUtc = entry.LastWriteTimeUtc,
            });
        }

        return result;
    }

    public IReadOnlyList<ScannedEntry> ScanSelection(IEnumerable<string> paths)
    {
        var result = new List<ScannedEntry>();

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                result.Add(new ScannedEntry
                {
                    SourcePath = info.FullName,
                    RelativePath = string.Empty,
                    FileName = info.Name,
                    FileSize = info.Length,
                    LastWriteTimeUtc = info.LastWriteTimeUtc,
                });
            }
            else if (Directory.Exists(path))
            {
                var folderPath = Path.GetFullPath(path);
                var rootName = new DirectoryInfo(folderPath).Name;

                foreach (var entry in EnumerateFolder(folderPath))
                {
                    var relativeDirectory = Path.GetRelativePath(folderPath,
                        Path.GetDirectoryName(entry.SourcePath)!);
                    if (relativeDirectory == ".") relativeDirectory = string.Empty;

                    result.Add(new ScannedEntry
                    {
                        SourcePath = entry.SourcePath,
                        RelativePath = string.IsNullOrEmpty(relativeDirectory)
                            ? rootName
                            : Path.Combine(rootName, relativeDirectory),
                        FileName = entry.FileName,
                        FileSize = entry.FileSize,
                        LastWriteTimeUtc = entry.LastWriteTimeUtc,
                    });
                }
            }
            else
            {
                _logger.LogWarning("跳过不存在的路径: {Path}", path);
            }
        }

        return result;
    }

    /// <summary>递归枚举文件，跳过重解析点。</summary>
    private IEnumerable<ScannedEntry> EnumerateFolder(string folderPath)
    {
        var pending = new Stack<string>();
        pending.Push(folderPath);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            string[] files;
            string[] directories;

            try
            {
                files = Directory.GetFiles(current);
                directories = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                           or PathTooLongException or DirectoryNotFoundException)
            {
                // 单个子目录不可读不应中断整个扫描
                _logger.LogWarning(ex, "无法读取目录，已跳过: {Directory}", current);
                continue;
            }

            foreach (var file in files)
            {
                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (!info.Exists) continue;
                    // 跳过重解析点文件
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "读取文件信息失败，已跳过: {File}", file);
                    continue;
                }

                yield return new ScannedEntry
                {
                    SourcePath = info.FullName,
                    FileName = info.Name,
                    FileSize = info.Length,
                    LastWriteTimeUtc = info.LastWriteTimeUtc,
                };
            }

            foreach (var directory in directories)
            {
                try
                {
                    var dirInfo = new DirectoryInfo(directory);
                    if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        _logger.LogDebug("跳过重解析点目录: {Directory}", directory);
                        continue;
                    }

                    pending.Push(directory);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "读取目录信息失败，已跳过: {Directory}", directory);
                }
            }
        }
    }
}
