using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppUpdateSettingsServiceTests
{
    [Fact]
    public void Save_WritesReloadableSettingsWithoutLeavingTemporaryFiles()
    {
        var rootPath = CreateTempDirectory();
        var settingsPath = Path.Combine(rootPath, "update-settings.json");
        var service = new AppUpdateSettingsService(settingsPath);

        service.Save(new AppUpdateSettings { DownloadUpdatesAutomatically = true });

        var reloaded = new AppUpdateSettingsService(settingsPath).Load();
        Assert.True(reloaded.DownloadUpdatesAutomatically);
        Assert.Empty(Directory.EnumerateFiles(rootPath, "update-settings.json.*.tmp"));
    }

    [Fact]
    public void Load_WhenSettingsAreCorrupt_QuarantinesCorruptFileAndReturnsDefaults()
    {
        var rootPath = CreateTempDirectory();
        var settingsPath = Path.Combine(rootPath, "update-settings.json");
        File.WriteAllText(settingsPath, "not json");
        var service = new AppUpdateSettingsService(settingsPath);

        var settings = service.Load();

        Assert.False(settings.DownloadUpdatesAutomatically);
        Assert.False(File.Exists(settingsPath));
        Assert.Single(Directory.EnumerateFiles(rootPath, "update-settings.json.corrupt.*"));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
