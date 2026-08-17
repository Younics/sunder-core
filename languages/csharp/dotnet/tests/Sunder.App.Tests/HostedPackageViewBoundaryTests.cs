using Avalonia;
using Avalonia.Controls;
using System.Collections.Specialized;
using Sunder.App.Services;
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
        Assert.Null(boundary.HostedView);
        Assert.DoesNotContain(hostedView, boundary.Children);
        var fallback = Assert.IsType<Border>(Assert.Single(boundary.Children));
        Assert.DoesNotContain(
            "measure failed",
            Assert.IsType<TextBlock>(fallback.Child).Text,
            StringComparison.OrdinalIgnoreCase);
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
        Assert.Null(boundary.HostedView);
        Assert.False(hostedView.IsDisposed);
    }

    [Fact]
    public void QuarantinePackage_WhenBoundaryDetachmentFails_ReportsCleanupAsUnproven()
    {
        var hostedView = new Control();
        var boundary = new HostedPackageViewBoundary(
            "agent",
            "agent.chat",
            hostedView,
            (_, _, _) => { });
        boundary.Measure(new Size(100, 100));
        ((INotifyCollectionChanged)boundary.Children).CollectionChanged +=
            static (_, _) => throw new InvalidOperationException(
                "boundary detach failed");

        var cleanupOutcome = boundary.QuarantinePackage();

        Assert.Equal(AppPackageUiCleanupOutcome.Unproven, cleanupOutcome);
        Assert.False(HostedPackageViewBoundary.IsVisualCleanupProven(hostedView));
    }

    [Fact]
    public void Measure_WhenHostedViewThrowsFatalFault_DoesNotReportOrReplaceView()
    {
        var hostedView = new FatalMeasureView();
        var reportCount = 0;
        var boundary = new HostedPackageViewBoundary(
            "agent",
            "agent.chat",
            hostedView,
            (_, _, _) => reportCount++);

        Assert.Throws<OutOfMemoryException>(() => boundary.Measure(new Size(100, 100)));

        Assert.Equal(0, reportCount);
        Assert.False(boundary.IsFaulted);
        Assert.Same(hostedView, boundary.HostedView);
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

    private sealed class FatalMeasureView : Control
    {
        protected override Size MeasureOverride(Size availableSize)
            => throw new OutOfMemoryException("fatal package fault");
    }
}
