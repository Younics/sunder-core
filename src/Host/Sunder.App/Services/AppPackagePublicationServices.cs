using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackagePublicationServices(
    IPackageShellViewService? shellViewService,
    IPackageSettingsNavigationService? settingsNavigationService,
    AppPackageGenerationPublication publication)
    : IPackageShellViewService, IPackageSettingsNavigationService
{
    public IReadOnlyList<PackageHotbarView> ListHotbarViews()
        => publication.IsPublished && shellViewService is not null ? shellViewService.ListHotbarViews() : [];

    public bool IsViewInHotbar(string viewId)
        => publication.IsPublished && shellViewService?.IsViewInHotbar(viewId) == true;

    public ValueTask<bool> AddViewToDefaultHotbarAsync(
        string viewId,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => shellViewService switch
        {
            AppPackageShellViewService service => service.AddViewToDefaultHotbarAsync(
                viewId, openPanel, parameters, publication, cancellationToken),
            not null when publication.IsPublished => shellViewService.AddViewToDefaultHotbarAsync(
                viewId, openPanel, parameters, cancellationToken),
            _ => CancelOrReturnFalse(cancellationToken),
        };

    public ValueTask<bool> AddViewToHotbarAsync(
        string viewId,
        PackageViewPlacement placement,
        int? index = null,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => shellViewService switch
        {
            AppPackageShellViewService service => service.AddViewToHotbarAsync(
                viewId, placement, index, openPanel, parameters, publication, cancellationToken),
            not null when publication.IsPublished => shellViewService.AddViewToHotbarAsync(
                viewId, placement, index, openPanel, parameters, cancellationToken),
            _ => CancelOrReturnFalse(cancellationToken),
        };

    public ValueTask<bool> RemoveViewFromHotbarAsync(string viewId, CancellationToken cancellationToken = default)
        => shellViewService switch
        {
            AppPackageShellViewService service => service.RemoveViewFromHotbarAsync(
                viewId, publication, cancellationToken),
            not null when publication.IsPublished => shellViewService.RemoveViewFromHotbarAsync(viewId, cancellationToken),
            _ => CancelOrReturnFalse(cancellationToken),
        };

    public ValueTask<bool> OpenViewPanelAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => shellViewService switch
        {
            AppPackageShellViewService service => service.OpenViewPanelAsync(
                viewId, parameters, publication, cancellationToken),
            not null when publication.IsPublished => shellViewService.OpenViewPanelAsync(
                viewId, parameters, cancellationToken),
            _ => CancelOrReturnFalse(cancellationToken),
        };

    public ValueTask<bool> CloseViewPanelAsync(string viewId, CancellationToken cancellationToken = default)
        => shellViewService switch
        {
            AppPackageShellViewService service => service.CloseViewPanelAsync(
                viewId, publication, cancellationToken),
            not null when publication.IsPublished => shellViewService.CloseViewPanelAsync(viewId, cancellationToken),
            _ => CancelOrReturnFalse(cancellationToken),
        };

    public ValueTask<bool> OpenSettingsAsync(
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => settingsNavigationService switch
        {
            AppPackageSettingsNavigationService service => service.OpenSettingsAsync(
                parameters, publication, cancellationToken),
            not null when publication.IsPublished => settingsNavigationService.OpenSettingsAsync(
                parameters, cancellationToken),
            _ => CancelOrReturnFalse(cancellationToken),
        };

    public ValueTask<bool> OpenPackageSettingsAsync(
        string packageId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => settingsNavigationService switch
        {
            AppPackageSettingsNavigationService service => service.OpenPackageSettingsAsync(
                packageId, parameters, publication, cancellationToken),
            not null when publication.IsPublished => settingsNavigationService.OpenPackageSettingsAsync(
                packageId, parameters, cancellationToken),
            _ => CancelOrReturnFalse(cancellationToken),
        };

    private static ValueTask<bool> CancelOrReturnFalse(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}
