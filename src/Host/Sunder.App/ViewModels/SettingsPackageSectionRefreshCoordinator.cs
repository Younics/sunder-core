namespace Sunder.App.ViewModels;

internal sealed class SettingsPackageSectionRefreshCoordinator(
    SettingsPackageSectionsViewModel packageSettings,
    Func<bool> isDisposed,
    Func<string?> getSelectedPackageId,
    Action<string?> preserveSelectionAfterRefresh,
    Action notifyPackageSectionsChanged,
    Action<bool> setIsBusy,
    Action<string> setStatusText)
{
    private readonly object _gate = new();
    private int _loadVersion;

    public Task CurrentLoadTask { get; private set; } = Task.CompletedTask;

    public Task RefreshAsync(bool preserveSelection, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!CurrentLoadTask.IsCompleted)
            {
                return CurrentLoadTask;
            }

            CurrentLoadTask = LoadAsync(preserveSelection, cancellationToken);
            return CurrentLoadTask;
        }
    }

    public void Invalidate()
    {
        _loadVersion++;
    }

    private async Task LoadAsync(bool preserveSelection, CancellationToken cancellationToken)
    {
        var loadVersion = ++_loadVersion;
        cancellationToken.ThrowIfCancellationRequested();
        setIsBusy(true);
        var selectedPackageId = preserveSelection ? getSelectedPackageId() : null;
        try
        {
            await packageSettings.LoadSectionsAsync(cancellationToken);
            if (!IsCurrent(loadVersion))
            {
                return;
            }

            notifyPackageSectionsChanged();
            preserveSelectionAfterRefresh(selectedPackageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrent(loadVersion))
            {
                setStatusText(ex.Message);
            }
        }
        finally
        {
            if (IsCurrent(loadVersion))
            {
                setIsBusy(false);
            }
        }
    }

    private bool IsCurrent(int loadVersion)
        => !isDisposed() && loadVersion == _loadVersion;
}
