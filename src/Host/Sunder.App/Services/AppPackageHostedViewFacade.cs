using Avalonia.Controls;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed class AppPackageHostedViewFacade(
    AppPackageViewRegistry viewRegistry,
    Func<string, bool> isPackageDisabled,
    Action<string, string, Exception> reportHostedViewFailure)
{
    private readonly object _navigationSyncRoot = new();
    private readonly Dictionary<string, CancellationTokenSource> _navigationCancellations = new(StringComparer.OrdinalIgnoreCase);

    public Control? GetOrCreateView(string viewId)
        => viewRegistry.GetOrCreateView(viewId, isPackageDisabled, reportHostedViewFailure);

    public Control? ReloadView(string viewId)
    {
        CancelViewNavigation(viewId);
        viewRegistry.RemoveCachedView(viewId);
        return GetOrCreateView(viewId);
    }

    public bool InvalidateView(string viewId)
    {
        CancelViewNavigation(viewId);
        return viewRegistry.RemoveCachedView(viewId);
    }

    public async ValueTask NotifyViewNavigatedAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        CancellationToken cancellationToken)
    {
        var currentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previousCancellation;
        lock (_navigationSyncRoot)
        {
            _navigationCancellations.Remove(viewId, out previousCancellation);
            _navigationCancellations[viewId] = currentCancellation;
        }

        TryCancel(previousCancellation);
        try
        {
            var view = GetOrCreateView(viewId);
            if (view is not null)
            {
                try
                {
                    await AppPackageViewNavigator.NotifyViewNavigatedAsync(view, viewId, parameters, currentCancellation.Token);
                }
                catch (OperationCanceledException) when (currentCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var packageId = viewRegistry.GetPackageId(viewId);
                    if (packageId is not null)
                    {
                        reportHostedViewFailure(packageId, $"Package view '{viewId}' navigation failed: {ex.Message}", ex);
                    }
                    else
                    {
                        AppSessionLog.WriteError($"Package view '{viewId}' navigation failed.", ex);
                    }
                }
            }
        }
        finally
        {
            lock (_navigationSyncRoot)
            {
                if (_navigationCancellations.TryGetValue(viewId, out var activeCancellation)
                    && ReferenceEquals(activeCancellation, currentCancellation))
                {
                    _navigationCancellations.Remove(viewId);
                }
            }

            currentCancellation.Dispose();
        }
    }

    public void CancelViewNavigation(string viewId)
    {
        CancellationTokenSource? cancellation;
        lock (_navigationSyncRoot)
        {
            _navigationCancellations.Remove(viewId, out cancellation);
        }

        TryCancel(cancellation);
    }

    public void CancelAllViewNavigations()
    {
        CancellationTokenSource[] cancellations;
        lock (_navigationSyncRoot)
        {
            cancellations = _navigationCancellations.Values.ToArray();
            _navigationCancellations.Clear();
        }

        foreach (var cancellation in cancellations)
        {
            TryCancel(cancellation);
        }
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Package view navigation cancellation callback failed.", ex);
        }
    }

    public bool HasSettingsView(string packageId)
        => viewRegistry.HasSettingsView(packageId);

    public IReadOnlyList<PackageSettingsViewDescriptor> ListSettingsViewPackages()
        => viewRegistry.ListSettingsViewPackages(isPackageDisabled);

    public Control? GetOrCreateSettingsView(string packageId)
        => viewRegistry.GetOrCreateSettingsView(packageId, isPackageDisabled, reportHostedViewFailure);

    public IReadOnlyList<PackageViewDescriptor> GetPackageViewDescriptors(string packageId)
        => viewRegistry.GetPackageViewDescriptors(packageId);
}
