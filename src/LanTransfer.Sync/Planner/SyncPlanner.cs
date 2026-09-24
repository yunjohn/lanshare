using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;
using LanTransfer.Core.Interfaces;
using LanTransfer.Sync.Models;

namespace LanTransfer.Sync.Planner;

/// <summary>
/// 双向同步核心规划器。
/// 必须同时比较 Baseline / Local / Remote 三者，只比较 Local 与 Remote 会导致
/// 「旧文件覆盖新文件」「删除文件重新出现」「重复传输」等严重问题。
/// 本类为纯逻辑，无 IO，便于单元测试。
/// </summary>
public sealed class SyncPlanner
{
    /// <summary>基线中不存在的标记。</summary>
    public const string Absent = "\u0000ABSENT";

    /// <summary>已删除（墓碑）标记。</summary>
    public const string Deleted = "\u0000DELETED";

    /// <summary>目录标记（目录没有内容哈希）。</summary>
    public const string DirectoryMarker = "\u0000DIR";

    public SyncPlan Plan(string syncPairId, IReadOnlyDictionary<string, SyncEntry> baseline,
        IReadOnlyList<LocalFileEntry> local, IReadOnlyList<SyncManifestEntry> remote, SyncMode mode,
        string localDeviceTag, DateTimeOffset? now = null,
        IReadOnlySet<string>? unreadableLocalPaths = null,
        IReadOnlySet<string>? unreadableRemotePaths = null)
    {
        var plan = new SyncPlan { SyncPairId = syncPairId };
        var timestamp = now ?? DateTimeOffset.UtcNow;

        var localMap = local.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
        var remoteMap = new Dictionary<string, SyncManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in remote)
            remoteMap[entry.RelativePath] = entry;

        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in baseline.Keys) allPaths.Add(key);
        foreach (var key in localMap.Keys) allPaths.Add(key);
        foreach (var key in remoteMap.Keys) allPaths.Add(key);

        foreach (var path in allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            // 本机扫描时读不到的条目（文件被其它程序占用、无权限等）状态是「未知」：
            // 必须整条跳过——它们既不在 local 里（会被误判为「本机已删除」），
            // 也不该被同步。跳过即保持不变，等解锁后的重试轮次再处理。
            if (unreadableLocalPaths is not null &&
                DirectoryScanResult.IsWithin(path, unreadableLocalPaths))
            {
                plan.SkippedUnreadable.Add(path);
                continue;
            }

            // 对端读不到的条目对称处理：不能因为对端清单里没有就当成「对端已删除」
            if (unreadableRemotePaths is not null &&
                DirectoryScanResult.IsWithin(path, unreadableRemotePaths))
            {
                plan.SkippedUnreadable.Add(path);
                continue;
            }

            var baselineEntry = baseline.TryGetValue(path, out var b) ? b : null;
            var localEntry = localMap.TryGetValue(path, out var l) ? l : null;
            var remoteEntry = remoteMap.TryGetValue(path, out var r) ? r : null;

            var isDirectory = (localEntry?.IsDirectory ?? false) ||
                              (remoteEntry?.IsDirectory ?? false) ||
                              (baselineEntry?.IsDirectory ?? false);

            var baseState = BaseState(baselineEntry, isDirectory);
            var localState = LocalState(localEntry, baselineEntry, isDirectory);
            var remoteState = RemoteState(remoteEntry, baselineEntry, isDirectory);

            var action = Decide(path, isDirectory, baseState, localState, remoteState, mode, localDeviceTag,
                timestamp);

            if (action.Type == SyncActionType.None) continue;

            // 带上内容哈希：Download/冲突取对端清单的哈希，Upload 取本机扫描的哈希。
            // 冲突去重与「上传期间文件被改」都依赖它。
            action.Sha256 = action.Type switch
            {
                SyncActionType.Download => remoteEntry?.Sha256,
                SyncActionType.Upload => localEntry?.Sha256,
                _ => remoteEntry?.Sha256,
            };

            // 目录只承担结构语义：
            // 1) 协议里没有「在对端创建目录」的接口，若为目录写基线，下一轮就会把
            //    「对端清单里没有该目录」判成「对端已删除」→ DeleteLocal(recursive: true)，
            //    把本机目录连同尚未上传成功的文件一起删掉（真实数据丢失路径）；
            // 2) 目录级删除/修复都是递归操作，风险远大于收益——文件级动作已足以表达删除语义。
            // 因此目录只保留「Upload」这一条无害的跟踪动作（引擎侧不写基线、不建目录，
            // 待目录内文件在对端落盘后自然收敛），其余动作一律抑制。
            if (isDirectory && action.Type != SyncActionType.Upload) continue;

            plan.Actions.Add(action);
            if (action.Type is SyncActionType.Conflict or SyncActionType.DeleteModifyConflict)
                plan.Conflicts.Add(action);
        }

        return plan;
    }

    private static SyncAction Decide(string path, bool isDirectory, string baseState, string localState,
        string remoteState, SyncMode mode, string localDeviceTag, DateTimeOffset timestamp)
    {
        if (baseState == localState && localState == remoteState)
            return None(path, isDirectory, "两端与基线一致");

        if (localState == baseState)
        {
            // 只有对端发生变化
            if (remoteState == Deleted)
                return mode == SyncMode.SendOnly
                    ? None(path, isDirectory, "单向同步模式忽略对端删除")
                    : Action(path, isDirectory, SyncActionType.DeleteLocal, "对端删除了该文件，本机未修改");

            if (remoteState == Absent)
                return None(path, isDirectory, "对端不存在且基线也没有");

            return mode == SyncMode.SendOnly
                ? None(path, isDirectory, "单向发送模式忽略对端修改")
                : Action(path, isDirectory, SyncActionType.Download, "对端修改，本机未修改");
        }

        if (remoteState == baseState)
        {
            // 只有本机发生变化
            if (localState == Deleted)
                return mode == SyncMode.ReceiveOnly
                    ? None(path, isDirectory, "单向同步模式忽略本机删除")
                    : Action(path, isDirectory, SyncActionType.DeleteRemote, "本机删除了该文件，对端未修改");

            if (localState == Absent)
                return None(path, isDirectory, "本机不存在且基线也没有");

            return mode == SyncMode.ReceiveOnly
                ? None(path, isDirectory, "单向接收模式忽略本机修改")
                : Action(path, isDirectory, SyncActionType.Upload, "本机修改，对端未修改");
        }

        if (localState == remoteState)
            return Action(path, isDirectory, SyncActionType.UpdateBaseline, "两端修改结果一致，仅更新基线");

        // 删除 / 修改冲突：绝不能直接删除被修改的一方
        if (localState == Deleted)
        {
            return new SyncAction
            {
                RelativePath = path,
                IsDirectory = isDirectory,
                Type = SyncActionType.DeleteModifyConflict,
                Reason = "本机已删除该文件，但对端修改了它：保留对端版本并记录冲突",
                ConflictCopyRelativePath = BuildConflictName(path, localDeviceTag, timestamp),
            };
        }

        if (remoteState == Deleted)
        {
            return new SyncAction
            {
                RelativePath = path,
                IsDirectory = isDirectory,
                Type = SyncActionType.DeleteModifyConflict,
                Reason = "对端已删除该文件，但本机修改了它：保留本机版本并记录冲突",
                ConflictCopyRelativePath = BuildConflictName(path, localDeviceTag, timestamp),
            };
        }

        // 双方都修改且结果不同：禁止默认覆盖任意一边
        return new SyncAction
        {
            RelativePath = path,
            IsDirectory = isDirectory,
            Type = SyncActionType.Conflict,
            Reason = "两端都修改了该文件且结果不同，已保留双方版本",
            ConflictCopyRelativePath = BuildConflictName(path, localDeviceTag, timestamp),
        };
    }

    /// <summary>生成冲突副本名，例如 report (conflict-PC-B-20260924-154501).docx</summary>
    public static string BuildConflictName(string relativePath, string deviceTag, DateTimeOffset timestamp)
    {
        var directory = Path.GetDirectoryName(relativePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var extension = Path.GetExtension(relativePath);

        var safeTag = new string(deviceTag.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (string.IsNullOrEmpty(safeTag)) safeTag = "remote";

        var fileName = $"{name} (conflict-{safeTag}-{timestamp:yyyyMMdd-HHmmss}){extension}";

        return string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
    }

    private static string BaseState(SyncEntry? entry, bool isDirectory)    {
        if (entry is null) return Absent;
        if (entry.Deleted) return Deleted;
        if (isDirectory) return DirectoryMarker;
        return entry.Sha256;
    }

    private static string LocalState(LocalFileEntry? entry, SyncEntry? baseline, bool isDirectory)
    {
        if (entry is null) return baseline is null ? Absent : Deleted;
        if (isDirectory) return DirectoryMarker;
        return entry.Sha256 ?? string.Empty;
    }

    private static string RemoteState(SyncManifestEntry? entry, SyncEntry? baseline, bool isDirectory)
    {
        if (entry is null) return baseline is null ? Absent : Deleted;
        if (entry.Deleted) return Deleted;
        if (isDirectory) return DirectoryMarker;
        return entry.Sha256 ?? string.Empty;
    }

    private static SyncAction None(string path, bool isDirectory, string reason) => new()
    {
        RelativePath = path,
        IsDirectory = isDirectory,
        Type = SyncActionType.None,
        Reason = reason,
    };

    private static SyncAction Action(string path, bool isDirectory, SyncActionType type, string reason) => new()
    {
        RelativePath = path,
        IsDirectory = isDirectory,
        Type = type,
        Reason = reason,
    };
}
