using System.Text.Json;

namespace Sunder.App.Services;

public sealed class AppUpdateSettings
{
    public bool DownloadUpdatesAutomatically { get; set; }
}

public sealed class AppUpdateSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsFilePath;
    private readonly object _syncRoot = new();

    public AppUpdateSettingsService(string? settingsFilePath = null)
    {
        _settingsFilePath = settingsFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sunder",
            "update-settings.json");
    }

    public AppUpdateSettings Load()
    {
        lock (_syncRoot)
        {
            if (!File.Exists(_settingsFilePath))
            {
                return new AppUpdateSettings();
            }

            try
            {
                return JsonSerializer.Deserialize<AppUpdateSettings>(File.ReadAllText(_settingsFilePath), JsonOptions)
                    ?? new AppUpdateSettings();
            }
            catch
            {
                QuarantineCorruptSettings();
                return new AppUpdateSettings();
            }
        }
    }

    public void Save(AppUpdateSettings settings)
    {
        lock (_syncRoot)
        {
            var directory = Path.GetDirectoryName(_settingsFilePath)!;
            Directory.CreateDirectory(directory);

            var tempFilePath = Path.Combine(directory, $"{Path.GetFileName(_settingsFilePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tempFilePath, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(tempFilePath, _settingsFilePath, overwrite: true);
            }
            catch
            {
                TryDeleteTempFile(tempFilePath);
                throw;
            }
        }
    }

    public Task SaveAsync(AppUpdateSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new AppUpdateSettings
        {
            DownloadUpdatesAutomatically = settings.DownloadUpdatesAutomatically,
        };
        return Task.Run(() => Save(snapshot), cancellationToken);
    }

    private void QuarantineCorruptSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                File.Move(_settingsFilePath, $"{_settingsFilePath}.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}");
            }
        }
        catch
        {
            // Corrupt update settings should never block app startup.
        }
    }

    private static void TryDeleteTempFile(string tempFilePath)
    {
        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to delete a temporary update settings file.", ex);
        }
    }
}
