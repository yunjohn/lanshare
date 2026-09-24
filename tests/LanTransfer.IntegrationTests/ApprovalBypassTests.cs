using System.Net;
using LanTransfer.Common.Protocol;
using LanTransfer.IntegrationTests.Support;
using Xunit;

namespace LanTransfer.IntegrationTests;

/// <summary>
/// 「未经用户确认不得写盘」这条边界的端到端回归测试。
///
/// <para>
/// 历史缺陷：<c>/transfers/{id}/approve</c> 早就被禁用了（远程调用一律 403，
/// 见 <c>LoopbackTransferTests.RemoteApprove_IsRejected</c>），但 <b>/resume 是同一个洞的另一扇门</b>：
/// 它调用 <c>SetState(Transferring, allowResumeFromTerminal: true)</c>，
/// 而 <c>SetState</c> 一旦把状态置为 Transferring 就会顺带把 <c>Approved</c> 置为 true。
/// WaitingApproval 不是终态，所以这道「终态不可复活」的守卫根本拦不住它。
/// </para>
///
/// <para>
/// 触发条件（局域网内任意一台主机，无需配对）：
/// <list type="number">
/// <item>POST /transfers 建一个传输 —— 接收端弹出确认框，用户还没点；</item>
/// <item>POST /transfers/{id}/resume —— 状态被推进到 Transferring、Approved 被置为 true；</item>
/// <item>PUT 分块 + POST /complete —— 文件直接写进下载目录，全程没有任何确认。</item>
/// </list>
/// 修复：<c>SetState</c> 拒绝把「未获用户确认」（<c>Approved == false</c>）的传输推进到接收中。
/// </para>
/// </summary>
public sealed class ApprovalBypassTests : IDisposable
{
    private readonly string _root;

    public ApprovalBypassTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lantransfer-approval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RemoteResume_CannotBypassUserApproval()
    {
        // 未配对设备 + 关闭自动接收 + 用户「不回答」确认框：这是最普通的陌生人发文件场景
        await using var h = await ServerClientHarness.StartAsync(_root);

        var source = h.CreateSourceFile("bypass.bin", 4096);
        var transferId = Guid.NewGuid().ToString();

        var create = await h.Client.CreateTransferAsync(IPAddress.Loopback.ToString(), h.Port,
            h.NewRequest(transferId, source));

        Assert.True(create.Success, create.Message);
        Assert.Equal("waiting-approval", create.State);
        Assert.Equal(1, h.PromptCount);

        // 攻击者试图用 /resume 把自己的传输「解锁」，然后照常上传、校验、落盘
        var resume = await h.Client.ResumeAsync(IPAddress.Loopback.ToString(), h.Port, transferId);

        var status = await h.Client.GetTransferStatusAsync(IPAddress.Loopback.ToString(), h.Port,
            transferId, 0);

        var bytes = await File.ReadAllBytesAsync(source);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

        using var stream = new MemoryStream(bytes);
        var chunk = await h.Client.UploadChunkAsync(IPAddress.Loopback.ToString(), h.Port, transferId, 0, 0,
            stream, bytes.Length);

        var complete = await h.Client.CompleteFileAsync(IPAddress.Loopback.ToString(), h.Port, transferId,
            new CompleteTransferRequest { FileIndex = 0, Sha256 = sha });

        // 一次把「有没有被绕过」的证据收集齐再断言：修复前后失败信息的差别一眼可见
        var problems = new List<string>();

        if (!string.Equals(resume.State, "waiting-approval", StringComparison.OrdinalIgnoreCase))
            problems.Add($"/resume 把状态改成了 {resume.State}");

        if (!string.Equals(status.State, "waiting-approval", StringComparison.OrdinalIgnoreCase))
            problems.Add($"/status 报出 {status.State}");

        if (chunk.Success) problems.Add("未经用户确认就接受了分块");

        if (complete.Success) problems.Add("未经用户确认就通过了校验");

        if (File.Exists(h.DownloadedPath("bypass.bin")))
            problems.Add("未经用户确认的文件被写进了接收目录");

        Assert.True(problems.Count == 0, "用户确认流程被绕过：" + string.Join("；", problems));
    }

    [Fact]
    public async Task EmptyFile_CannotBeCommittedWithoutApproval()
    {
        // 最省事的一条绕过路径：0 字节文件的 TotalChunks == 0，
        // 连一个分块都不用传 —— 直接 /complete 就能把文件「校验通过」并写进接收目录。
        await using var h = await ServerClientHarness.StartAsync(_root);

        var source = h.CreateSourceFile("empty.bin", 0);
        var transferId = Guid.NewGuid().ToString();

        var create = await h.Client.CreateTransferAsync(IPAddress.Loopback.ToString(), h.Port,
            h.NewRequest(transferId, source));

        Assert.True(create.Success, create.Message);
        Assert.Equal("waiting-approval", create.State);

        var emptySha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>()));

        var complete = await h.Client.CompleteFileAsync(IPAddress.Loopback.ToString(), h.Port, transferId,
            new CompleteTransferRequest { FileIndex = 0, Sha256 = emptySha });

        Assert.False(complete.Success, "未经用户确认的传输不得通过校验");
        Assert.False(File.Exists(h.DownloadedPath("empty.bin")),
            "未经用户确认的空文件被写进了接收目录（确认流程被绕过）");
    }

    [Fact]
    public async Task EmptyFile_StillWorksAfterApproval()
    {
        // 对照用例：0 字节文件本身是合法输入，确认之后必须能正常落盘
        await using var h = await ServerClientHarness.StartAsync(_root, autoRespondToPrompts: true);

        var source = h.CreateSourceFile("empty-approved.bin", 0);

        var (create, _, complete) = await h.PushFileAsync(Guid.NewGuid().ToString(), source);

        Assert.True(create.Success, create.Message);
        Assert.True(complete.Success, complete.Message);
        Assert.True(File.Exists(h.DownloadedPath("empty-approved.bin")));
    }

    [Fact]
    public async Task PausedTransfer_CannotBeCommittedUntilResumed()
    {
        // 「分块收完」和「/complete」之间状态是会变的：用户在接收端点了暂停/取消，
        // 发送端这时把最后一步 /complete 发过来，文件同样不该落盘。
        await using var h = await ServerClientHarness.StartAsync(_root, autoRespondToPrompts: true);

        var source = h.CreateSourceFile("paused-complete.bin", 4096);
        var transferId = Guid.NewGuid().ToString();

        var create = await h.Client.CreateTransferAsync(IPAddress.Loopback.ToString(), h.Port,
            h.NewRequest(transferId, source));

        Assert.True(create.Success, create.Message);
        await h.WaitForStateAsync(transferId, "transferring");

        var bytes = await File.ReadAllBytesAsync(source);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

        using (var stream = new MemoryStream(bytes))
        {
            var chunk = await h.Client.UploadChunkAsync(IPAddress.Loopback.ToString(), h.Port, transferId, 0,
                0, stream, bytes.Length);

            Assert.True(chunk.Success, chunk.Message);
        }

        var pause = await h.Client.PauseAsync(IPAddress.Loopback.ToString(), h.Port, transferId);
        Assert.Equal("paused", pause.State);

        var blocked = await h.Client.CompleteFileAsync(IPAddress.Loopback.ToString(), h.Port, transferId,
            new CompleteTransferRequest { FileIndex = 0, Sha256 = sha });

        Assert.False(blocked.Success, "已暂停的传输不得通过 /complete 落盘");
        Assert.False(File.Exists(h.DownloadedPath("paused-complete.bin")));

        // 对照：继续之后必须能正常收尾（不能把暂停变成「永远写不进去」）
        var resume = await h.Client.ResumeAsync(IPAddress.Loopback.ToString(), h.Port, transferId);
        Assert.Equal("transferring", resume.State);

        var complete = await h.Client.CompleteFileAsync(IPAddress.Loopback.ToString(), h.Port, transferId,
            new CompleteTransferRequest { FileIndex = 0, Sha256 = sha });

        Assert.True(complete.Success, complete.Message);
        Assert.True(File.Exists(h.DownloadedPath("paused-complete.bin")));
    }

    [Fact]
    public async Task UserApproval_StillAllowsTheWrite()
    {
        // 对照用例：证明上面的拦截不是「把正常传输也一起挡了」。
        await using var h = await ServerClientHarness.StartAsync(_root, autoRespondToPrompts: true);

        var source = h.CreateSourceFile("approved.bin", 4096);

        var (create, chunk, complete) = await h.PushFileAsync(Guid.NewGuid().ToString(), source);

        Assert.True(create.Success, create.Message);
        Assert.True(chunk.Success, chunk.Message);
        Assert.True(complete.Success, complete.Message);
        Assert.Equal(1, h.PromptCount);
        Assert.True(File.Exists(h.DownloadedPath("approved.bin")));
    }

    [Fact]
    public async Task PausedApprovedTransfer_CanStillBeResumed()
    {
        // 对照用例：暂停/继续是正常功能（对端断流后用户点「继续」），不能被这条守卫挡住。
        await using var h = await ServerClientHarness.StartAsync(_root, autoRespondToPrompts: true);

        var source = h.CreateSourceFile("resumed.bin", 4096);
        var transferId = Guid.NewGuid().ToString();

        var create = await h.Client.CreateTransferAsync(IPAddress.Loopback.ToString(), h.Port,
            h.NewRequest(transferId, source));

        Assert.True(create.Success, create.Message);

        // 审批是异步推进的：等状态真的变成 transferring 之后再暂停/继续
        await h.WaitForStateAsync(transferId, "transferring");

        var pause = await h.Client.PauseAsync(IPAddress.Loopback.ToString(), h.Port, transferId);
        Assert.Equal("paused", pause.State);

        var resume = await h.Client.ResumeAsync(IPAddress.Loopback.ToString(), h.Port, transferId);
        Assert.Equal("transferring", resume.State);

        var bytes = await File.ReadAllBytesAsync(source);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

        using var stream = new MemoryStream(bytes);
        var chunk = await h.Client.UploadChunkAsync(IPAddress.Loopback.ToString(), h.Port, transferId, 0, 0,
            stream, bytes.Length);

        Assert.True(chunk.Success, chunk.Message);

        var complete = await h.Client.CompleteFileAsync(IPAddress.Loopback.ToString(), h.Port, transferId,
            new CompleteTransferRequest { FileIndex = 0, Sha256 = sha });

        Assert.True(complete.Success, complete.Message);
        Assert.True(File.Exists(h.DownloadedPath("resumed.bin")));
    }
}
