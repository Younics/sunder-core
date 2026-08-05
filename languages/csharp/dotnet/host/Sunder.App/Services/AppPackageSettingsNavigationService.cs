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

    public ValueTask<bool> OpenSettingsAsync(
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => OpenSettingsCoreAsync(
            PackageNavigationParameters.Snapshot(parameters),
            publication: null,
            cancellationToken);

    internal ValueTask<bool> OpenSettingsAsync(
        IReadOnlyDictionary<string, string?>? parameters,
        AppPackageGenerationPublication publication,
        CancellationToken cancellationToken)
        => OpenSettingsCoreAsync(
            PackageNavigationParameters.Snapshot(parameters),
            publication,
            cancellationToken);

    public ValueTask<bool> OpenPackageSettingsAsync(
        string packageId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => OpenPackageSettingsCoreAsync(
            packageId,
            PackageNavigationParameters.Snapshot(parameters),
            publication: null,
            cancellationToken);

    internal ValueTask<bool> OpenPackageSettingsAsync(
        string packageId,
        IReadOnlyDictionary<string, string?>? parameters,
        AppPackageGenerationPublication publication,
        CancellationToken cancellationToken)
        => OpenPackageSettingsCoreAsync(
            packageId,
            PackageNavigationParameters.Snapshot(parameters),
            publication,
            cancellationToken);

    private async ValueTask<bool> OpenSettingsCoreAsync(
        IReadOnlyDictionary<string, string?> parameters,
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

            launcher.ShowSettings(parameters);
            return true;
        });
    }

    private async ValueTask<bool> OpenPackageSettingsCoreAsync(
        string packageId,
        IReadOnlyDictionary<string, string?> parameters,
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
