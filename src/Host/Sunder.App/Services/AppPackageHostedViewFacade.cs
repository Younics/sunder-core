using Avalonia.Controls;

namespace Sunder.App.Services;

internal sealed class AppPackageHostedViewFacade(
    AppPackageViewRegistry viewRegistry,
    Func<string, bool> isPackageDisabled,
    Action<string, string, Exception> reportHostedViewFailure)
{
    public Control? GetOrCreateView(string viewId)
        => viewRegistry.GetOrCreateView(viewId, isPackageDisabled, reportHostedViewFailure);

    public Control? ReloadView(string viewId)
    {
        viewRegistry.RemoveCachedView(viewId);
        return GetOrCreateView(viewId);
    }

    public bool InvalidateView(string viewId)
        => viewRegistry.RemoveCachedView(viewId);

    public async ValueTask NotifyViewNavigatedAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        CancellationToken cancellationToken)
    {
        var view = GetOrCreateView(viewId);
        if (view is null)
        {
            return;
        }

        await AppPackageViewNavigator.NotifyViewNavigatedAsync(view, viewId, parameters, cancellationToken);
    }

    public bool HasSettingsView(string packageId)
        => viewRegistry.HasSettingsView(packageId);

    public IReadOnlyList<PackageSettingsViewDescriptor> ListSettingsViewPackages()
        => viewRegistry.ListSettingsViewPackages(isPackageDisabled);

    public Control? GetOrCreateSettingsView(string packageId)
        => viewRegistry.GetOrCreateSettingsView(packageId, isPackageDisabled, reportHostedViewFailure);
}
