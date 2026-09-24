using LanTransfer.Common.Constants;
using LanTransfer.Core.Interfaces;
using LanTransfer.Sync.Models;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Sync.Scanner;

/// <summary>
/// 同步目录全量扫描。
/// 采用两级检测（第 93 节）：先用 FileSize + LastWriteTimeUtc 快速比对基线，
/// 只有疑似变化时才计算 SHA-256，避免反复对大文件做全量哈希。
/// </summary>
public sealed class DirectoryScanner
{
    private readonly IHashService _hash;
    private readonly ILogger<DirectoryScanner> _logger;

    public DirectoryScanner(IHashService hash, ILogger<DirectoryScanner> logger)
    {
        _hash = hash;
        _logger = logger;
    }

    public async Task<DirectoryScanResult> ScanAsync(string rootPath,
        IReadOnlyDictionary<string, Core.Interfaces.SyncEntry> baseline, bool fullHash,
        CancellationToken cancellationToken = default)
    {
        var result = new List<LocalFileEntry>();
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(rootPath))
        {
            // 根不存在 = 整棵树状态未知。绝不能返回「空目录」：规划器会把两端所有文件判成已删除。
            _logger.LogWarning("同步目录不存在，本轮整棵树状态未知: {Root}", rootPath);
            return new DirectoryScanResult { RootUnreadable = true };
        }

        var pending = new Stack<string>();
        pending.Push(rootPath);
        var rootUnreadable = false;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            string[] files;
            string[] directories;

            try
            {
                files = Directory.GetFiles(current);
                directories = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                           or DirectoryNotFoundException or PathTooLongException)
            {
                // 整个目录读不到：其下所有条目状态未知，绝不能当成删除
                _logger.LogWarning(ex, "无法读取同步目录，本轮按「未知」处理: {Directory}", current);

                if (string.Equals(current, rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    // 根目录枚举失败 = 整棵树未知：必须让调用方直接放弃本轮，
                    // 不能继续往下扫并把结果当成「目录里什么都没有」。
                    rootUnreadable = true;
                    break;
                }

                unreadable.Add(Relative(rootPath, current));
                continue;
            }

            foreach (var directory in directories)
            {
                try
                {
                    var info = new DirectoryInfo(directory);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                    result.Add(new LocalFileEntry
                    {
                        RelativePath = Relative(rootPath, info.FullName),
                        IsDirectory = true,
                        FileSize = 0,
                        LastWriteTimeUtc = info.LastWriteTimeUtc,
                    });

                    pending.Push(directory);
                }
                catch (Exception ex)
                {
                    // 读不到子目录信息：同样按未知处理，避免误删对端
                    _logger.LogWarning(ex, "读取目录信息失败，本轮按「未知」处理: {Directory}", directory);
                    unreadable.Add(Relative(rootPath, directory));
                }
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 传输/下载的临时文件绝不能当正式内容同步出去：
                // 崩溃或取消后会留下 *.part / *.part.json（可能只有半个文件），
                // 一旦被上传，对端拿到的是损坏的「正式文件」。
                if (IsTransferArtifact(file)) continue;

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (!info.Exists) continue;
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "读取文件信息失败，本轮按「未知」处理: {File}", file);
                    unreadable.Add(Relative(rootPath, file));
                    continue;
                }

                var relativePath = Relative(rootPath, info.FullName);

                var entry = new LocalFileEntry
                {
                    RelativePath = relativePath,
                    IsDirectory = false,
                    FileSize = info.Length,
                    LastWriteTimeUtc = info.LastWriteTimeUtc,
                };

                var known = baseline.TryGetValue(relativePath, out var knownEntry) ? knownEntry : null;

                var needHash = fullHash ||
                               known is null ||
                               known.Deleted ||
                               known.FileSize != entry.FileSize ||
                               Math.Abs((known.LastWriteTimeUtc - entry.LastWriteTimeUtc).TotalSeconds) >= 2;

                if (needHash)
                {
                    try
                    {
                        entry.Sha256 = await _hash.ComputeFileHashAsync(info.FullName, null, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // 文件被独占锁定（正被其它程序打开）或读取失败：
                        // 本轮状态未知——既不下发同步，也绝不能被当成「已删除」，否则会删掉对端副本。
                        // 记入 UnreadablePaths，由规划器跳过，并在解锁后的重试轮次里正常同步。
                        _logger.LogWarning(ex, "计算同步文件摘要失败，本轮按「未知」处理: {File}", info.FullName);
                        unreadable.Add(relativePath);
                        continue;
                    }
                }
                else
                {
                    entry.Sha256 = known!.Sha256;
                }

                result.Add(entry);
            }
        }

        if (rootUnreadable)
        {
            // 根读不到时结果必须整体作废：调用方（SyncEngine）看到 RootUnreadable 会直接放弃本轮
            return new DirectoryScanResult { RootUnreadable = true };
        }

        return new DirectoryScanResult { Entries = result, UnreadablePaths = unreadable };
    }

    /// <summary>
    /// 是否为传输/下载过程产生的临时文件（*.part、*.part.json、*.part.json.tmp）。
    /// </summary>
    private static bool IsTransferArtifact(string path)
    {
        var name = Path.GetFileName(path);

        return name.EndsWith(AppConstants.PartExtension, StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(AppConstants.PartExtension + ".json", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(AppConstants.PartExtension + ".json.tmp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>计算相对于同步根的规范化路径（使用 '\' 分隔）。</summary>
    public static string Relative(string rootPath, string fullPath)    {
        var relative = Path.GetRelativePath(rootPath, fullPath);
        if (relative == ".") return string.Empty;
        return relative.Replace('/', '\\');
    }

    /// <summary>把相对路径安全地还原为绝对路径；越界返回 null。</summary>
    public static string? Resolve(ISafePathResolver resolver, string rootPath, string relativePath)
        => resolver.TryResolve(rootPath, relativePath, out var full) ? full : null;
}
