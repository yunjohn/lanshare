using LanTransfer.IntegrationTests.Support;
using Xunit;

namespace LanTransfer.IntegrationTests;

/// <summary>
/// 「自动接收可信设备文件」这条设置的端到端回归测试。
///
/// <para>
/// 历史缺陷：接收端注册表确实按设置自动放行了传输（状态直接变 Transferring、审批任务已完成），
/// 但服务端**无条件**为每个新传输抛出 <c>IncomingTransferRequested</c> 事件。
/// 后果有两个：
/// </para>
/// <list type="number">
/// <item>设置形同虚设 —— 用户勾了「自动接收可信设备文件」，对方发文件时本机**照样**弹确认框；</item>
/// <item>更糟的是，此时传输已经在接收中了，用户在弹窗上点「拒绝」会把 <c>RespondToApproval</c>
/// 打到这个已放行的传输上（状态被改成 Rejected），正在上传的文件随即失败。</item>
/// </list>
/// <para>
/// 修复：只有「真的在等用户点头」的传输（<c>IncomingTransferState.IsAwaitingUserDecision</c>）
/// 才抛确认事件。
/// </para>
/// </summary>
public sealed class AutoAcceptTrustedTransferTests : IDisposable
{
    private readonly string _root;

    public AutoAcceptTrustedTransferTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lantransfer-autoaccept-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TrustedDeviceWithAutoAccept_DoesNotAskForConfirmation()
    {
        await using var h = await ServerClientHarness.StartAsync(_root, autoAccept: true, trustClient: true);

        var source = h.CreateSourceFile("auto-accepted.bin", 4096);
        var response = await h.Client.CreateTransferAsync("127.0.0.1", h.Port, h.NewRequest("auto", source));

        Assert.True(response.Success, $"{response.ErrorCode} {response.Message}");

        // 设置生效：不弹确认框，而且传输已经直接进入接收状态
        Assert.Equal(0, h.PromptCount);
        Assert.Equal("transferring", response.State);
    }

    [Fact]
    public async Task UntrustedDevice_StillAsksForConfirmation()
    {
        // 对照组：证明上面那条断言不是「永远不会弹窗」的空测试。
        // 同一个自动接收设置下，未配对设备仍然必须弹窗。
        await using var h = await ServerClientHarness.StartAsync(_root, autoAccept: true, trustClient: false);

        var source = h.CreateSourceFile("needs-approval.bin", 4096);
        var response = await h.Client.CreateTransferAsync("127.0.0.1", h.Port, h.NewRequest("ask", source));

        Assert.True(response.Success, $"{response.ErrorCode} {response.Message}");
        Assert.Equal(1, h.PromptCount);
        Assert.Equal("waiting-approval", response.State);
    }
}
