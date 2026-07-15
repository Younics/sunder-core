using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal sealed class SettingsPackageSectionRefreshCoordinator(
    SettingsPackageSectionsViewModel packageSettings,
    Func<bool> isDisposed,
    Func<string?> getSelectedPackageId,
    Action<string?> preserveSelectionAfterRefresh,
    Action notifyPackageSectionsChanged,
    Action<bool> setIsBusy,
    Action<string> setStatusText)
    : IDisposable
{
    private readonly LatestAsyncRequest _requests = new();
    private readonly object _gate = new();

    public Task CurrentLoadTask { get; private set; } = Task.CompletedTask;

    public Task<bool> RefreshAsync(bool preserveSelection, CancellationToken cancellationToken)
    {
        var request = _requests.Start(cancellationToken);
        var task = LoadAsync(request, preserveSelection);
        lock (_gate)
        {
            CurrentLoadTask = task;
        }

        return task;
    }

    public void Invalidate() => _requests.Invalidate();

    public void Dispose() => _requests.Dispose();

    private async Task<bool> LoadAsync(LatestAsyncRequest.Lease request, bool preserveSelection)
    {
        using (request)
        {
            setIsBusy(true);
            var selectedPackageId = preserveSelection ? getSelectedPackageId() : null;
            try
            {
                var result = await packageSettings.LoadSectionsAsync(request.Token);
                if (!IsCurrent(request))
                {
                    return false;
                }

                packageSettings.ApplySections(result);
                notifyPackageSectionsChanged();
                preserveSelectionAfterRefresh(selectedPackageId);
                return true;
            }
            catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                if (IsCurrent(request))
                {
                    request.Fail(ex);
                    setStatusText(ex.Message);
                }

                return false;
            }
            finally
            {
                if (IsCurrent(request))
                {
                    setIsBusy(false);
                }
            }
        }
    }

    private bool IsCurrent(LatestAsyncRequest.Lease request)
        => !isDisposed() && request.IsCurrent;
}
