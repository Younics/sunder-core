namespace Sunder.App.Services;

public interface IWindowLauncher
{
    void ShowSettings();

    Task<bool> ShowPackageSettingsAsync(
        string packageId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default);

    void ShowPackages();

    void ShowDeveloperLogs();

    void CloseForShutdown();
}
