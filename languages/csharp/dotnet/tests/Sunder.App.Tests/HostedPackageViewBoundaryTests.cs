using Avalonia;
using Avalonia.Controls;
using Sunder.App.Views.Controls;
using Xunit;

namespace Sunder.App.Tests;

public sealed class HostedPackageViewBoundaryTests
{
    [Fact]
    public void Measure_WhenHostedViewThrows_ReportsFailureOnceAndDetachesView()
    {
        var hostedView = new ThrowingMeasureView();
        var failures = new List<(string PackageId, string ViewId, Exception Exception)>();
        var boundary = new HostedPackageViewBoundary(
            "agent",
            "agent.chat",
            hostedView,
            (packageId, viewId, exception) => failures.Add((packageId, viewId, exception)));

        boundary.Measure(new Size(100, 100));
        boundary.Measure(new Size(100, 100));

        var failure = Assert.Single(failures);
        Assert.Equal("agent", failure.PackageId);
        Assert.Equal("agent.chat", failure.ViewId);
        Assert.Equal("measure failed", failure.Exception.Message);
        Assert.True(boundary.IsFaulted);
        Assert.DoesNotContain(hostedView, boundary.Children);
    }

    [Fact]
    public void ReleaseHostedView_DetachesHostedViewWithoutDisposingIt()
    {
        var hostedView = new DisposableView();
        var boundary = new HostedPackageViewBoundary(
            "agent",
            "agent.chat",
            hostedView,
            (_, _, _) => { });
        boundary.Measure(new Size(100, 100));

        HostedPackageViewBoundary.ReleaseHostedView(boundary);

        Assert.Empty(boundary.Children);
        Assert.False(hostedView.IsDisposed);
    }

    private sealed class ThrowingMeasureView : Control
    {
        protected override Size MeasureOverride(Size availableSize)
            => throw new InvalidOperationException("measure failed");
    }

    private sealed class DisposableView : Control, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
