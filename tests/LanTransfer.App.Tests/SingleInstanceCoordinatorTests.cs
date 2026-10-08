using LanTransfer.App.Services;
using Xunit;

namespace LanTransfer.App.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public async Task SecondInstance_NotifiesPrimaryInsteadOfAcquiringMutex()
    {
        var id = "LanTransfer.Tests." + Guid.NewGuid().ToString("N");
        using var primary = new SingleInstanceCoordinator(id);
        using var secondary = new SingleInstanceCoordinator(id);

        Assert.True(primary.TryAcquire());

        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(() => activated.TrySetResult());

        Assert.False(secondary.TryAcquire());
        await secondary.NotifyPrimaryAsync();

        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
