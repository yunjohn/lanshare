using LanTransfer.Common.Models;
using Xunit;

namespace LanTransfer.Core.Tests;

/// <summary>统一状态机：终态 / 可恢复 / 活动状态判定与协议字符串往返。</summary>
public class TransferStateTests
{
    [Theory]
    [InlineData(TransferState.Completed, true)]
    [InlineData(TransferState.Rejected, true)]
    [InlineData(TransferState.Cancelled, true)]
    [InlineData(TransferState.Failed, true)]
    [InlineData(TransferState.VerificationFailed, true)]
    [InlineData(TransferState.Transferring, false)]
    [InlineData(TransferState.Paused, false)]
    [InlineData(TransferState.Pending, false)]
    public void IsTerminal_MatchesSpecification(TransferState state, bool expected)
        => Assert.Equal(expected, state.IsTerminal());

    [Theory]
    [InlineData(TransferState.Paused, true)]
    [InlineData(TransferState.Failed, true)]
    [InlineData(TransferState.Cancelled, true)]
    [InlineData(TransferState.Pending, true)]
    [InlineData(TransferState.Queued, true)]
    [InlineData(TransferState.Completed, false)]
    [InlineData(TransferState.Rejected, false)]
    [InlineData(TransferState.VerificationFailed, false)]
    public void IsResumable_MatchesSpecification(TransferState state, bool expected)
        => Assert.Equal(expected, state.IsResumable());

    [Theory]
    [InlineData(TransferState.Preparing, true)]
    [InlineData(TransferState.Transferring, true)]
    [InlineData(TransferState.Verifying, true)]
    [InlineData(TransferState.WaitingApproval, true)]
    [InlineData(TransferState.Paused, false)]
    [InlineData(TransferState.Completed, false)]
    [InlineData(TransferState.Failed, false)]
    public void IsActive_MatchesSpecification(TransferState state, bool expected)
        => Assert.Equal(expected, state.IsActive());

    [Fact]
    public void TerminalAndActiveAreDisjoint()
    {
        foreach (var state in Enum.GetValues<TransferState>())
        {
            Assert.False(state.IsTerminal() && state.IsActive(), $"{state} 同时被判定为终态与活动态");
        }
    }

    [Fact]
    public void WireString_RoundTripsForEveryState()
    {
        foreach (var state in Enum.GetValues<TransferState>())
        {
            var wire = state.ToWireString();
            Assert.NotEqual("unknown", wire);
            Assert.Equal(state, TransferStateExtensions.FromWireString(wire));
        }
    }

    [Theory]
    [InlineData("pending", TransferState.Pending)]
    [InlineData("waiting-approval", TransferState.WaitingApproval)]
    [InlineData("verification-failed", TransferState.VerificationFailed)]
    public void FromWireString_ParsesKnownValues(string wire, TransferState expected)
        => Assert.Equal(expected, TransferStateExtensions.FromWireString(wire));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void FromWireString_FallsBackToFailed(string? wire)
        => Assert.Equal(TransferState.Failed, TransferStateExtensions.FromWireString(wire));
}
