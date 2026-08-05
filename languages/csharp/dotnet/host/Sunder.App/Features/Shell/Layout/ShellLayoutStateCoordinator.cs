using Sunder.App.Models;
using Sunder.App.Services;

namespace Sunder.App.Features.Shell.Layout;

internal sealed class ShellLayoutStateCoordinator : IDisposable
{
    private readonly ShellState _shellState;
    private readonly ShellStatePersistenceQueue _shellStatePersistenceQueue;
    private bool _persistenceEnabled;

    public ShellLayoutStateCoordinator(
        ShellStateService shellStateService,
        ShellState shellState,
        TimeSpan saveDelay,
        bool persistenceEnabled = true)
    {
        _shellState = shellState;
        _shellStatePersistenceQueue = new ShellStatePersistenceQueue(
            shellStateService,
            _shellState,
            saveDelay);
        _persistenceEnabled = persistenceEnabled;
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
        QueueSaveIfEnabled();
    }

    public void PersistPreferredRuntimeUrl(Uri runtimeUrl)
    {
        _shellState.PreferredRuntimeUrl = runtimeUrl.AbsoluteUri;
        QueueSaveIfEnabled();
    }

    public void PersistBackgroundProcessPopoverSize(double width, double height)
    {
        _shellState.BackgroundProcessPopoverWidth = width;
        _shellState.BackgroundProcessPopoverHeight = height;
        QueueSaveIfEnabled();
    }

    public void EnablePersistence()
        => _persistenceEnabled = true;

    public void Dispose()
    {
        if (_persistenceEnabled)
        {
            _shellStatePersistenceQueue.SaveImmediately();
        }
        _shellStatePersistenceQueue.Dispose();
    }

    private void QueueSaveIfEnabled()
    {
        if (_persistenceEnabled)
        {
            _ = _shellStatePersistenceQueue.QueueSave();
        }
    }
}
