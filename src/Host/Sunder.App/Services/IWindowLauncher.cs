namespace Sunder.App.Services;

public interface IWindowLauncher
{
    void ShowSettings(IReadOnlyDictionary<string, string?>? parameters = null);

    Task<bool> ShowPackageSettingsAsync(
        string packageId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default);

    void ShowPackages();

    void ShowStacks();

    void ShowDeveloperLogs();

    void CloseForShutdown();
}
