using Sunder.Protocol;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

public sealed class DevPackageHotReloadSession : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan StabilityProbeDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxStabilityWait = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<string> _folders;
    private readonly IRuntimeApiClientFactory _runtimeApiClientFactory;
    private readonly WindowLauncher _windowLauncher;
    private readonly DeveloperLogService _developerLog;
    private readonly NotificationCenterService _notificationCenter;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Dictionary<string, FileSystemWatcher> _folderWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _parentWatchers = [];
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _debounceCts;
    private bool _reloadQueued;
    private bool _disposed;

    public DevPackageHotReloadSession(
        IReadOnlyList<string> folders,
        IRuntimeApiClientFactory runtimeApiClientFactory,
        WindowLauncher windowLauncher,
        DeveloperLogService developerLog,
        NotificationCenterService notificationCenter,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _folders = folders.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _runtimeApiClientFactory = runtimeApiClientFactory;
        _windowLauncher = windowLauncher;
        _developerLog = developerLog;
        _notificationCenter = notificationCenter;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public void Start()
    {
        _developerLog.Enable();
        _developerLog.Info("dev.hot_reload", $"Watching {_folders.Count} dev package folder(s).");
        CreateParentWatchers();
        RefreshFolderWatchers();
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        foreach (var watcher in _folderWatchers.Values)
        {
            watcher.Dispose();
        }

        _folderWatchers.Clear();
        foreach (var watcher in _parentWatchers)
        {
            watcher.Dispose();
        }

        _parentWatchers.Clear();
        _reloadGate.Dispose();
    }

    private void CreateParentWatchers()
    {
        foreach (var parentFolder in _folders
                     .Select(Path.GetDirectoryName)
                     .Where(static folder => !string.IsNullOrWhiteSpace(folder))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(parentFolder))
            {
                continue;
            }

            var watcher = CreateWatcher(parentFolder, includeSubdirectories: false);
            _parentWatchers.Add(watcher);
        }
    }

    private void RefreshFolderWatchers()
    {
        foreach (var folder in _folders)
        {
            if (!Directory.Exists(folder))
            {
                if (_folderWatchers.Remove(folder, out var existingWatcher))
                {
                    existingWatcher.Dispose();
                }

                continue;
            }

            if (_folderWatchers.ContainsKey(folder))
            {
                continue;
            }

            _folderWatchers[folder] = CreateWatcher(folder, includeSubdirectories: true);
        }
    }

    private FileSystemWatcher CreateWatcher(string folder, bool includeSubdirectories)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.FileName
                           | NotifyFilters.DirectoryName
                           | NotifyFilters.LastWrite
                           | NotifyFilters.Size
                           | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        watcher.Changed += Watcher_OnChanged;
        watcher.Created += Watcher_OnChanged;
        watcher.Deleted += Watcher_OnChanged;
        watcher.Renamed += Watcher_OnRenamed;
        watcher.Error += Watcher_OnError;
        return watcher;
    }

    private void Watcher_OnChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnorePath(e.FullPath))
        {
            return;
        }

        ScheduleReload($"{e.ChangeType}: {e.FullPath}");
    }

    private void Watcher_OnRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldIgnorePath(e.FullPath) && ShouldIgnorePath(e.OldFullPath))
        {
            return;
        }

        ScheduleReload($"Renamed: {e.OldFullPath} -> {e.FullPath}");
    }

    private void Watcher_OnError(object sender, ErrorEventArgs e)
    {
        _developerLog.Warning("dev.hot_reload", $"File watcher error: {e.GetException().Message}");
        ScheduleReload("watcher error");
    }

    private void ScheduleReload(string reason)
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _developerLog.Info("dev.hot_reload", $"Change detected ({reason}).");
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            _ = DebounceAndReloadAsync(_debounceCts.Token);
        }
    }

    private async Task DebounceAndReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _delayAsync(DebounceDelay, cancellationToken).ConfigureAwait(false);
            await RunReloadLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await ReportFailureAsync($"Dev package hot reload failed: {ex.Message}").ConfigureAwait(false);
            AppSessionLog.WriteError("Dev package hot reload failed.", ex);
        }
    }

    private async Task RunReloadLoopAsync(CancellationToken cancellationToken)
    {
        if (!await _reloadGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            lock (_syncRoot)
            {
                _reloadQueued = true;
            }

            return;
        }

        try
        {
            do
            {
                lock (_syncRoot)
                {
                    _reloadQueued = false;
                }

                await ReloadOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            while (ShouldRunQueuedReload());
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private bool ShouldRunQueuedReload()
    {
        lock (_syncRoot)
        {
            return !_disposed && _reloadQueued;
        }
    }

    private async Task ReloadOnceAsync(CancellationToken cancellationToken)
    {
        RefreshFolderWatchers();
        if (!await WaitForStableFoldersAsync(cancellationToken).ConfigureAwait(false))
        {
            _developerLog.Warning("dev.hot_reload", "Dev package output was not stable yet; waiting for another file change.");
            return;
        }

        _developerLog.Info("dev.hot_reload", "Staging dev package reload.");
        using var runtimeApiClient = _runtimeApiClientFactory.CreateClient();
        var runtimeStage = await runtimeApiClient.StageDevPackagesAsync(_folders, cancellationToken).ConfigureAwait(false);
        LogMessages(runtimeStage.Warnings, PackageLogLevel.Warning);
        LogMessages(runtimeStage.Errors, PackageLogLevel.Error);
        if (runtimeStage.StageId is null || runtimeStage.Errors.Count > 0)
        {
            await ReportFailureAsync("Runtime rejected the dev package reload.").ConfigureAwait(false);
            return;
        }

        AppPackageHostStage? appStage = null;
        try
        {
            appStage = await _windowLauncher.StagePackageLifecycleAsync(
                runtimeStage.LoadedPackages,
                runtimeStage.PackageSources,
                cancellationToken).ConfigureAwait(false);

            var commitResult = await runtimeApiClient.CommitDevPackageStageAsync(runtimeStage.StageId, cancellationToken).ConfigureAwait(false);
            LogMessages(commitResult.Warnings, PackageLogLevel.Warning);
            LogMessages(commitResult.Errors, PackageLogLevel.Error);
            if (commitResult.Errors.Count > 0)
            {
                await appStage.Host.DisposeAsync();
                await ReportFailureAsync("Runtime failed to commit the dev package reload.").ConfigureAwait(false);
                return;
            }

            await _windowLauncher.CommitPackageLifecycleStageAsync(appStage, cancellationToken).ConfigureAwait(false);
            _developerLog.Info("dev.hot_reload", $"Dev packages reloaded at {DateTimeOffset.Now:T}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (appStage is not null)
            {
                await appStage.Host.DisposeAsync();
            }

            await runtimeApiClient.DiscardDevPackageStageAsync(runtimeStage.StageId, CancellationToken.None).ConfigureAwait(false);
            _developerLog.Error("dev.hot_reload", ex.Message);
            await ReportFailureAsync("App rejected the dev package reload.").ConfigureAwait(false);
        }
    }

    private async Task<bool> WaitForStableFoldersAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + MaxStabilityWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_folders.All(IsLoadableDevPackageFolder))
            {
                await _delayAsync(StabilityProbeDelay, cancellationToken).ConfigureAwait(false);
                RefreshFolderWatchers();
                continue;
            }

            if (!TrySnapshotFiles(out var before))
            {
                await _delayAsync(StabilityProbeDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await _delayAsync(StabilityProbeDelay, cancellationToken).ConfigureAwait(false);
            if (TrySnapshotFiles(out var after) && AreSnapshotsEqual(before, after))
            {
                return true;
            }
        }

        return false;
    }

    private bool TrySnapshotFiles(out Dictionary<string, FileSnapshot> snapshot)
    {
        snapshot = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var folder in _folders)
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (ShouldIgnorePath(file))
                    {
                        continue;
                    }

                    var info = new FileInfo(file);
                    snapshot[file] = new FileSnapshot(info.Length, info.LastWriteTimeUtc);
                }
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool AreSnapshotsEqual(
        IReadOnlyDictionary<string, FileSnapshot> left,
        IReadOnlyDictionary<string, FileSnapshot> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var entry in left)
        {
            if (!right.TryGetValue(entry.Key, out var rightSnapshot) || !entry.Value.Equals(rightSnapshot))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLoadableDevPackageFolder(string folder)
        => Directory.Exists(folder) && File.Exists(Path.Combine(folder, "sunder-package.json"));

    private static bool ShouldIgnorePath(string path)
    {
        var fileName = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName)
               || string.Equals(fileName, ".DS_Store", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".swp", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
    }

    private void LogMessages(IEnumerable<string> messages, PackageLogLevel level)
    {
        foreach (var message in messages)
        {
            _developerLog.Write(level, "dev.hot_reload", message);
        }
    }

    private async Task ReportFailureAsync(string message)
    {
        _developerLog.Error("dev.hot_reload", message);
        await _notificationCenter.PublishAsync(
            "sunder.app",
            "Sunder",
            new PackageNotificationRequest(
                "Dev package reload failed",
                message,
                PackageNotificationDisplayMode.ToastAndTray,
                PackageNotificationSeverity.Error)).ConfigureAwait(false);
        _windowLauncher.ShowDeveloperLogs();
    }

    private readonly record struct FileSnapshot(long Length, DateTime LastWriteTimeUtc);
}
