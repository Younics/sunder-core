using Avalonia.Controls;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class InitialShellRenderWaiterTests
{
    [Fact]
    public void ControlReadiness_WhenActiveControlChanges_StopsWaitingForDetachedSnapshot()
    {
        var detached = new Border();
        var replacement = new Border();
        IReadOnlyList<Control> activeControls = [detached];
        using var readiness = new InitialShellRenderWaiter.ControlReadiness(
            () => activeControls);

        Assert.True(readiness.IsTracking(detached));
        activeControls = [replacement];

        readiness.Refresh();

        Assert.False(readiness.IsTracking(detached));
        Assert.True(readiness.IsTracking(replacement));
    }
}
