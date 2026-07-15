using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

public sealed class AppPackageSettingsNavigationService(IUiDispatcher? uiDispatcher = null) : IPackageSettingsNavigationService
{
    private readonly IUiDispatcher _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
    private IWindowLauncher? _windowLauncher;

    public void Attach(IWindowLauncher windowLauncher)
        => _windowLauncher = windowLauncher;

    public void Detach(IWindowLauncher windowLauncher)
    {
        if (ReferenceEquals(_windowLauncher, windowLauncher))
        {
            _windowLauncher = null;
        }
    }

    public async ValueTask<bool> OpenSettingsAsync(
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => await OpenSettingsCoreAsync(parameters, publication: null, cancellationToken);

    internal ValueTask<bool> OpenSettingsAsync(
        IReadOnlyDictionary<string, string?>? parameters,
        AppPackageGenerationPublication publication,
        CancellationToken cancellationToken)
        => OpenSettingsCoreAsync(parameters, publication, cancellationToken);

    public async ValueTask<bool> OpenPackageSettingsAsync(
        string packageId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => await OpenPackageSettingsCoreAsync(packageId, parameters, publication: null, cancellationToken);

    internal ValueTask<bool> OpenPackageSettingsAsync(
        string packageId,
        IReadOnlyDictionary<string, string?>? parameters,
        AppPackageGenerationPublication publication,
        CancellationToken cancellationToken)
        => OpenPackageSettingsCoreAsync(packageId, parameters, publication, cancellationToken);

    private async ValueTask<bool> OpenSettingsCoreAsync(
        IReadOnlyDictionary<string, string?>? parameters,
        AppPackageGenerationPublication? publication,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _uiDispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (publication is not null && !publication.IsPublished)
            {
                return false;
            }

            var launcher = _windowLauncher;
            if (launcher is null)
            {
                return false;
            }

            launcher.ShowSettings();
            return true;
        });
    }

    private async ValueTask<bool> OpenPackageSettingsCoreAsync(
        string packageId,
        IReadOnlyDictionary<string, string?>? parameters,
        AppPackageGenerationPublication? publication,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        return await _uiDispatcher.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (publication is not null && !publication.IsPublished)
            {
                return false;
            }

            var launcher = _windowLauncher;
            return launcher is not null
                   && await launcher.ShowPackageSettingsAsync(packageId, parameters, cancellationToken);
        });
    }
}
