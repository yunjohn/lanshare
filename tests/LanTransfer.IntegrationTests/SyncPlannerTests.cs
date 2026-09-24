using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;
using LanTransfer.Core.Interfaces;
using LanTransfer.Sync.Models;
using LanTransfer.Sync.Planner;
using Xunit;

namespace LanTransfer.IntegrationTests;

/// <summary>
/// 双向同步核心规划算法测试（任务书第 77~104 节）。
/// 关键约束：必须同时比较 Baseline / Local / Remote 三者；
/// 禁止只比较 LastWriteTime；禁止让「删除」被误判为「新增」而复活；
/// 禁止双方都修改时默认覆盖任意一边。
/// </summary>
public class SyncPlannerTests
{
    private const string PairId = "pair-1";
    private const string Tag = "PC-B";

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T15:45:01+08:00");

    private readonly SyncPlanner _planner = new();

    // ---------------------------------------------------------------- 辅助构造

    private static SyncEntry Baseline(string path, string sha, bool deleted = false) => new()
    {
        SyncPairId = PairId,
        RelativePath = path,
        FileId = "file-" + path,
        Sha256 = sha,
        Version = 1,
        Deleted = deleted,
    };

    private static LocalFileEntry Local(string path, string? sha) => new()
    {
        RelativePath = path,
        FileSize = 100,
        Sha256 = sha,
        LastWriteTimeUtc = Now,
    };

    private static SyncManifestEntry Remote(string path, string? sha, bool deleted = false) => new()
    {
        RelativePath = path,
        FileId = "remote-" + path,
        FileSize = 100,
        Sha256 = sha,
        Deleted = deleted,
    };

    private SyncPlan Plan(Dictionary<string, SyncEntry> baseline, LocalFileEntry[] local,
        SyncManifestEntry[] remote, SyncMode mode = SyncMode.TwoWay)
        => _planner.Plan(PairId, baseline, local, remote, mode, Tag, Now);

    // ---------------------------------------------------------------- 基础场景

    [Fact]
    public void EmptyEverything_ProducesNoActions()
    {
        var plan = Plan(new(), Array.Empty<LocalFileEntry>(), Array.Empty<SyncManifestEntry>());

        Assert.Empty(plan.Actions);
        Assert.False(plan.HasWork);
    }

    [Fact]
    public void BothSidesIdenticalToBaseline_ProducesNoActions()
    {
        var baseline = new Dictionary<string, SyncEntry> { ["a.txt"] = Baseline("a.txt", "h1") };

        var plan = Plan(baseline, new[] { Local("a.txt", "h1") }, new[] { Remote("a.txt", "h1") });

        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void NewLocalFile_UploadsToRemote()
    {
        var plan = Plan(new(), new[] { Local("new.txt", "h1") }, Array.Empty<SyncManifestEntry>());

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.Upload, action.Type);
        Assert.Equal("new.txt", action.RelativePath);
    }

    [Fact]
    public void NewRemoteFile_DownloadsToLocal()
    {
        var plan = Plan(new(), Array.Empty<LocalFileEntry>(), new[] { Remote("new.txt", "h1") });

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.Download, action.Type);
        Assert.Equal("new.txt", action.RelativePath);
    }

    [Fact]
    public void RemoteModified_LocalUnchanged_Downloads()
    {
        var baseline = new Dictionary<string, SyncEntry> { ["a.txt"] = Baseline("a.txt", "h1") };

        var plan = Plan(baseline, new[] { Local("a.txt", "h1") }, new[] { Remote("a.txt", "h2") });

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.Download, action.Type);
    }

    [Fact]
    public void LocalModified_RemoteUnchanged_Uploads()
    {
        var baseline = new Dictionary<string, SyncEntry> { ["a.txt"] = Baseline("a.txt", "h1") };

        var plan = Plan(baseline, new[] { Local("a.txt", "h2") }, new[] { Remote("a.txt", "h1") });

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.Upload, action.Type);
    }

    [Fact]
    public void BothModifiedIdentically_OnlyUpdatesBaseline()
    {
        var baseline = new Dictionary<string, SyncEntry> { ["a.txt"] = Baseline("a.txt", "h1") };

        var plan = Plan(baseline, new[] { Local("a.txt", "h2") }, new[] { Remote("a.txt", "h2") });

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.UpdateBaseline, action.Type);
        Assert.Empty(plan.Conflicts);
    }

    // ---------------------------------------------------------------- 删除传播

    [Fact]
    public void LocalDeleted_RemoteUnchanged_DeletesRemote()
    {
        var baseline = new Dictionary<string, SyncEntry> { ["a.txt"] = Baseline("a.txt", "h1") };

        var plan = Plan(baseline, Array.Empty<LocalFileEntry>(), new[] { Remote("a.txt", "h1") });

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.DeleteRemote, action.Type);
    }

    // ---------------------------------------------------------------- 文件被占用（状态未知）

    /// <summary>
    /// 回归测试（数据安全）：本机扫描时读不到的文件（被其它程序打开、无权限）
    /// 状态是「未知」，绝不能被当成「本机已删除」——那样会生成 DeleteRemote，
    /// 把对端的副本删掉。
    /// </summary>
    [Fact]
    public void UnreadableLocalFile_ProducesNoAction_AndNeverDeletesRemote()
    {
        var baseline = new Dictionary<string, SyncEntry> { ["a.txt"] = Baseline("a.txt", "h1") };

        var plan = _planner.Plan(PairId, baseline,
            Array.Empty<LocalFileEntry>(),                 // 扫描时被占用 → 不在结果里
            new[] { Remote("a.txt", "h1") },               // 对端与基线一致
            SyncMode.TwoWay, Tag, Now,
            unreadableLocalPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.txt" });

        Assert.Empty(plan.Actions);
        Assert.False(plan.HasWork);
        Assert.Contains("a.txt", plan.SkippedUnreadable);
    }

    /// <summary>目录读不到时，其下所有条目的状态都未知，同样不得删除对端。</summary>
    [Fact]
    public void UnreadableDirectory_SkipsEverythingBelowIt()
    {
        var baseline = new Dictionary<string, SyncEntry>
        {
            ["docs\\a.txt"] = Baseline("docs\\a.txt", "h1"),
            ["docs\\deep\\b.txt"] = Baseline("docs\\deep\\b.txt", "h2"),
            ["root.txt"] = Baseline("root.txt", "h3"),
        };

        var plan = _planner.Plan(PairId, baseline,
            new[] { Local("root.txt", "h3") },
            new[] { Remote("docs\\a.txt", "h1"), Remote("docs\\deep\\b.txt", "h2"), Remote("root.txt", "h3") },
            SyncMode.TwoWay, Tag, Now,
            unreadableLocalPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "docs" });

        Assert.Empty(plan.Actions);
        Assert.Equal(2, plan.SkippedUnreadable.Count);
    }

    /// <summary>真正的删除必须继续传播：读不到 ≠ 删除，但「确实不见了」仍要同步删除。</summary>
    [Fact]
    public void GenuineDeletion_StillPropagates_WhenNotFlaggedUnreadable()
    {
        var baseline = new Dictionary<string, SyncEntry> { ["a.txt"] = Baseline("a.txt", "h1") };

        var plan = Plan(baseline, Array.Empty<LocalFileEntry>(), new[] { Remote("a.txt", "h1") });

        Assert.Equal(SyncActionType.DeleteRemote, Assert.Single(plan.Actions).Type);
        Assert.Empty(plan.SkippedUnreadable);
    }

    /// <summary>
    /// 回归测试（数据安全）：「根目录整体读不到」用空串作哨兵，
    /// 它必须匹配**所有**路径 —— 否则护栏形同虚设，会把对端整个同步目录判成已删除。
    /// </summary>
    [Fact]
    public void UnreadableRoot_SkipsEveryPath()
    {
        var baseline = new Dictionary<string, SyncEntry>
        {
            ["a.txt"] = Baseline("a.txt", "h1"),
            ["docs\\b.txt"] = Baseline("docs\\b.txt", "h2"),
        };

        var rootUnknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty };

        Assert.True(DirectoryScanResult.IsWithin("a.txt", rootUnknown));
        Assert.True(DirectoryScanResult.IsWithin("docs\\b.txt", rootUnknown));

        var plan = _planner.Plan(PairId, baseline, Array.Empty<LocalFileEntry>(),
            new[] { Remote("a.txt", "h1"), Remote("docs\\b.txt", "h2") },
            SyncMode.TwoWay, Tag, Now, unreadableLocalPaths: rootUnknown);

        Assert.Empty(plan.Actions);
        Assert.Equal(2, plan.SkippedUnreadable.Count);
    }

    /// <summary>对端读不到（清单里带未知标记）时同样必须跳过，不能判成「对端已删除」。</summary>
    [Fact]
    public void UnreadableRemotePath_IsSkippedInsteadOfDeletedLocally()
    {
        var baseline = new Dictionary<string, SyncEntry> { ["a.txt"] = Baseline("a.txt", "h1") };

        var plan = _planner.Plan(PairId, baseline,
            new[] { Local("a.txt", "h1") },
            Array.Empty<SyncManifestEntry>(),
            SyncMode.TwoWay, Tag, Now,
            unreadableRemotePaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.txt" });

        Assert.Empty(plan.Actions);
        Assert.Contains("a.txt", plan.SkippedUnreadable);
    }

    /// <summary>
    /// 回归测试（数据安全）：对端没有某个目录时，绝不能对本机目录下 DeleteLocal——
    /// 引擎会 Directory.Delete(recursive: true)，把本机目录连同里面尚未上传成功的文件一起删掉。
    /// </summary>
    [Fact]
    public void DirectoryMissingOnRemote_DoesNotDeleteLocalDirectory()
    {
        var baseline = new Dictionary<string, SyncEntry>
        {
            ["docs"] = new()
            {
                RelativePath = "docs",
                IsDirectory = true,
                Sha256 = string.Empty,
                Version = 1,
            },
        };

        var local = new[]
        {
            new LocalFileEntry { RelativePath = "docs", IsDirectory = true, LastWriteTimeUtc = Now },
        };

        var plan = Plan(baseline, local, Array.Empty<SyncManifestEntry>());

        Assert.DoesNotContain(plan.Actions, a => a.Type == SyncActionType.DeleteLocal);
        Assert.Empty(plan.LocalDeletions);
    }

    [Fact]
    public void RemoteDeleted_LocalUnchanged_DeletesLocal()
    {        var baseline = new Dictionary<string, SyncEntry> { ["a.txt"] = Baseline("a.txt", "h1") };

        var plan = Plan(baseline, new[] { Local("a.txt", "h1") }, new[] { Remote("a.txt", null, deleted: true) });

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.DeleteLocal, action.Type);
    }

    [Fact]
    public void BothDeleted_OnlyUpdatesBaseline_FileDoesNotResurrect()
    {
        var baseline = new Dictionary<string, SyncEntry> { ["a.txt"] = Baseline("a.txt", "h1") };

        var plan = Plan(baseline, Array.Empty<LocalFileEntry>(), new[] { Remote("a.txt", null, deleted: true) });

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.UpdateBaseline, action.Type);
    }

    [Fact]
    public void AlreadyDeletedTombstoneOnBothSides_ProducesNoActions()
    {
        var baseline = new Dictionary<string, SyncEntry>
        {
            ["a.txt"] = Baseline("a.txt", string.Empty, deleted: true),
        };

        var plan = Plan(baseline, Array.Empty<LocalFileEntry>(), new[] { Remote("a.txt", null, deleted: true) });

        Assert.Empty(plan.Actions);
    }

    // ---------------------------------------------------------------- 冲突

    [Fact]
    public void BothModifiedDifferently_ProducesConflictAndKeepsBothVersions()
    {
        var baseline = new Dictionary<string, SyncEntry> { ["report.docx"] = Baseline("report.docx", "h1") };

        var plan = Plan(baseline, new[] { Local("report.docx", "local") }, new[] { Remote("report.docx", "remote") });

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.Conflict, action.Type);
        Assert.Single(plan.Conflicts);

        Assert.Equal("report (conflict-PC-B-20260924-154501).docx", action.ConflictCopyRelativePath);
    }

    [Fact]
    public void LocalDeletedRemoteModified_IsDeleteModifyConflict_RemoteVersionWins()
    {
        var baseline = new Dictionary<string, SyncEntry> { ["a.txt"] = Baseline("a.txt", "h1") };

        var plan = Plan(baseline, Array.Empty<LocalFileEntry>(), new[] { Remote("a.txt", "h2") });

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.DeleteModifyConflict, action.Type);
        Assert.Contains("对端修改", action.Reason);
        Assert.NotNull(action.ConflictCopyRelativePath);
    }

    [Fact]
    public void RemoteDeletedLocalModified_IsDeleteModifyConflict_LocalVersionWins()
    {
        var baseline = new Dictionary<string, SyncEntry> { ["a.txt"] = Baseline("a.txt", "h1") };

        var plan = Plan(baseline, new[] { Local("a.txt", "h2") }, new[] { Remote("a.txt", null, deleted: true) });

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.DeleteModifyConflict, action.Type);
        Assert.Contains("本机修改", action.Reason);
    }

    [Fact]
    public void ConflictNeverSilentlyDeletesModifiedSide()
    {
        var baseline = new Dictionary<string, SyncEntry> { ["a.txt"] = Baseline("a.txt", "h1") };

        // 本机删除 + 对端修改：绝不能产生 DeleteLocal / DeleteRemote
        var plan = Plan(baseline, Array.Empty<LocalFileEntry>(), new[] { Remote("a.txt", "h2") });

        Assert.DoesNotContain(plan.Actions,
            a => a.Type is SyncActionType.DeleteLocal or SyncActionType.DeleteRemote);
    }

    // ---------------------------------------------------------------- 单向模式

    [Fact]
    public void SendOnly_IgnoresRemoteModificationAndDeletion()
    {
        var baseline = new Dictionary<string, SyncEntry>
        {
            ["modified.txt"] = Baseline("modified.txt", "h1"),
            ["deleted.txt"] = Baseline("deleted.txt", "h1"),
        };

        var plan = Plan(baseline,
            new[] { Local("modified.txt", "h1"), Local("deleted.txt", "h1") },
            new[] { Remote("modified.txt", "h2"), Remote("deleted.txt", null, deleted: true) },
            SyncMode.SendOnly);

        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void SendOnly_StillUploadsLocalChanges()
    {
        var baseline = new Dictionary<string, SyncEntry> { ["a.txt"] = Baseline("a.txt", "h1") };

        var plan = Plan(baseline, new[] { Local("a.txt", "h2") }, new[] { Remote("a.txt", "h1") },
            SyncMode.SendOnly);

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.Upload, action.Type);
    }

    [Fact]
    public void ReceiveOnly_IgnoresLocalModificationAndDeletion()
    {
        var baseline = new Dictionary<string, SyncEntry>
        {
            ["modified.txt"] = Baseline("modified.txt", "h1"),
            ["deleted.txt"] = Baseline("deleted.txt", "h1"),
        };

        var plan = Plan(baseline,
            new[] { Local("modified.txt", "h2"), Local("deleted.txt", "h1") },
            new[] { Remote("modified.txt", "h1"), Remote("deleted.txt", "h1") },
            SyncMode.ReceiveOnly);

        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void ReceiveOnly_StillDownloadsRemoteChanges()
    {
        var baseline = new Dictionary<string, SyncEntry> { ["a.txt"] = Baseline("a.txt", "h1") };

        var plan = Plan(baseline, new[] { Local("a.txt", "h1") }, new[] { Remote("a.txt", "h2") },
            SyncMode.ReceiveOnly);

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionType.Download, action.Type);
    }

    // ---------------------------------------------------------------- 目录与嵌套路径

    [Fact]
    public void NestedPaths_ArePlannedIndependently()
    {
        var baseline = new Dictionary<string, SyncEntry>
        {
            ["docs\\a.txt"] = Baseline("docs\\a.txt", "h1"),
            ["docs\\sub\\b.txt"] = Baseline("docs\\sub\\b.txt", "h1"),
        };

        var local = new[] { Local("docs\\a.txt", "h2"), Local("docs\\sub\\b.txt", "h1") };
        var remote = new[] { Remote("docs\\a.txt", "h1"), Remote("docs\\sub\\b.txt", "h1") };

        var plan = Plan(baseline, local, remote);

        var action = Assert.Single(plan.Actions);
        Assert.Equal("docs\\a.txt", action.RelativePath);
        Assert.Equal(SyncActionType.Upload, action.Type);
    }

    [Fact]
    public void ConflictName_PreservesDirectorySegment()
    {
        var baseline = new Dictionary<string, SyncEntry>
        {
            ["docs\\report.docx"] = Baseline("docs\\report.docx", "h1"),
        };

        var plan = Plan(baseline, new[] { Local("docs\\report.docx", "l") },
            new[] { Remote("docs\\report.docx", "r") });

        var action = Assert.Single(plan.Actions);
        Assert.Equal("docs\\report (conflict-PC-B-20260924-154501).docx", action.ConflictCopyRelativePath);
    }

    [Fact]
    public void ConflictName_SanitizesDeviceTag()
    {
        var name = SyncPlanner.BuildConflictName("a.txt", "PC B/../*", Now);

        Assert.Equal("a (conflict-PCB-20260924-154501).txt", name);
    }

    [Fact]
    public void ConflictName_FallsBackWhenTagIsEmpty()
    {
        var name = SyncPlanner.BuildConflictName("a.txt", "***", Now);

        Assert.Equal("a (conflict-remote-20260924-154501).txt", name);
    }

    [Fact]
    public void DirectoriesAreTrackedWithoutHashing()
    {
        var baseline = new Dictionary<string, SyncEntry>();
        var local = new[]
        {
            new LocalFileEntry { RelativePath = "empty", IsDirectory = true, LastWriteTimeUtc = Now },
        };

        var plan = Plan(baseline, local, Array.Empty<SyncManifestEntry>());

        var action = Assert.Single(plan.Actions);
        Assert.True(action.IsDirectory);
        Assert.Equal(SyncActionType.Upload, action.Type);
    }

    [Fact]
    public void MixedScenario_ProducesExactlyOneActionPerPath()
    {
        var baseline = new Dictionary<string, SyncEntry>
        {
            ["same.txt"] = Baseline("same.txt", "h"),
            ["push.txt"] = Baseline("push.txt", "h"),
            ["pull.txt"] = Baseline("pull.txt", "h"),
            ["conflict.txt"] = Baseline("conflict.txt", "h"),
            ["local-del.txt"] = Baseline("local-del.txt", "h"),
            ["remote-del.txt"] = Baseline("remote-del.txt", "h"),
        };

        var local = new[]
        {
            Local("same.txt", "h"),
            Local("push.txt", "h2"),
            Local("pull.txt", "h"),
            Local("conflict.txt", "l"),
            Local("remote-del.txt", "h"),
        };

        var remote = new[]
        {
            Remote("same.txt", "h"),
            Remote("push.txt", "h"),
            Remote("pull.txt", "h2"),
            Remote("conflict.txt", "r"),
            Remote("local-del.txt", "h"),
        };

        var plan = Plan(baseline, local, remote);

        Assert.Equal(5, plan.Actions.Count);
        Assert.Single(plan.Uploads);
        Assert.Single(plan.Downloads);
        Assert.Single(plan.RemoteDeletions);
        Assert.Single(plan.Conflicts);

        // 每个相对路径最多产生一个动作
        Assert.Equal(plan.Actions.Count,
            plan.Actions.Select(a => a.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Plan_IsDeterministicAndSortedByPath()
    {
        var baseline = new Dictionary<string, SyncEntry>();
        var local = new[]
        {
            Local("z.txt", "h"), Local("a.txt", "h"), Local("m.txt", "h"),
        };

        var plan = Plan(baseline, local, Array.Empty<SyncManifestEntry>());

        Assert.Equal(new[] { "a.txt", "m.txt", "z.txt" }, plan.Actions.Select(a => a.RelativePath));
    }

    [Fact]
    public void Plan_CarriesSyncPairId()
    {
        var plan = Plan(new(), Array.Empty<LocalFileEntry>(), Array.Empty<SyncManifestEntry>());

        Assert.Equal(PairId, plan.SyncPairId);
    }
}
