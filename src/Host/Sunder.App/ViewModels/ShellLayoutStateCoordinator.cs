using Sunder.App.Models;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal sealed class ShellLayoutStateCoordinator : IDisposable
{
    private readonly ShellState _shellState;
    private readonly ShellStatePersistenceQueue _shellStatePersistenceQueue;

    public ShellLayoutStateCoordinator(
        ShellStateService shellStateService,
        ShellState shellState,
        TimeSpan saveDelay)
    {
        _shellState = shellState;
        _shellStatePersistenceQueue = new ShellStatePersistenceQueue(
            shellStateService,
            () => ShellStateSnapshotFactory.Clone(_shellState),
            saveDelay);
    }

    public double AdjustLeftPanelWidth(double currentWidth, double delta, double maximumWidth)
        => ShellPanelSizing.ClampPanelWidth(currentWidth + delta, maximumWidth);

    public double AdjustRightPanelWidth(double currentWidth, double delta, double maximumWidth)
        => ShellPanelSizing.ClampPanelWidth(currentWidth + delta, maximumWidth);

    public double AdjustTopRowHeightRatio(double currentRatio, double deltaRatio)
        => ShellPanelSizing.ClampTopRowRatio(currentRatio + deltaRatio);

    public double AdjustBottomSplitRatio(double currentRatio, double deltaRatio)
        => ShellPanelSizing.ClampBottomSplitRatio(currentRatio + deltaRatio);

    public void PersistShellLayout(
        double leftPanelWidth,
        double rightPanelWidth,
        double topRowHeightRatio,
        double bottomSplitRatio)
    {
        _shellState.LayoutVersion = ShellState.CurrentLayoutVersion;
        _shellState.HasInitializedLayout = true;
        _shellState.LeftPanelWidth = leftPanelWidth;
        _shellState.RightPanelWidth = rightPanelWidth;
        _shellState.TopRowHeightRatio = topRowHeightRatio;
        _shellState.BottomSplitRatio = bottomSplitRatio;
        _shellStatePersistenceQueue.QueueSave();
    }

    public void PersistPreferredRuntimeUrl(Uri runtimeUrl)
    {
        _shellState.PreferredRuntimeUrl = runtimeUrl.AbsoluteUri;
        _shellStatePersistenceQueue.QueueSave();
    }

    public void PersistBackgroundProcessPopoverSize(double width, double height)
    {
        _shellState.BackgroundProcessPopoverWidth = width;
        _shellState.BackgroundProcessPopoverHeight = height;
        _shellStatePersistenceQueue.QueueSave();
    }

    public void Dispose()
    {
        _shellStatePersistenceQueue.SaveImmediately();
        _shellStatePersistenceQueue.Dispose();
    }
}
