using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

public sealed class AppPackageSettingsNavigationService : IPackageSettingsNavigationService
{
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
    {
        cancellationToken.ThrowIfCancellationRequested();
        var launcher = _windowLauncher;
        if (launcher is null)
        {
            return false;
        }

        return await UiThread.InvokeAsync(() =>
        {
            launcher.ShowSettings();
            return true;
        });
    }

    public async ValueTask<bool> OpenPackageSettingsAsync(
        string packageId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        var launcher = _windowLauncher;
        if (launcher is null)
        {
            return false;
        }

        return await UiThread.InvokeAsync(() => launcher.ShowPackageSettingsAsync(packageId, parameters, cancellationToken));
    }
}
