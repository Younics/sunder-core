using Sunder.Protocol;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

public sealed class DevPackageHotReloadSession : IDisposable
{
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
        if (DevPackageWatchSupport.ShouldIgnorePath(e.FullPath))
        {
            return;
        }

        ScheduleReload($"{e.ChangeType}: {e.FullPath}");
    }

    private void Watcher_OnRenamed(object sender, RenamedEventArgs e)
    {
        if (DevPackageWatchSupport.ShouldIgnorePath(e.FullPath) && DevPackageWatchSupport.ShouldIgnorePath(e.OldFullPath))
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
            await _delayAsync(DevPackageWatchSupport.DebounceDelay, cancellationToken).ConfigureAwait(false);
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
        if (!await DevPackageWatchSupport.WaitForStableFoldersAsync(
                _folders,
                _delayAsync,
                cancellationToken,
                onLoadabilityRetry: RefreshFolderWatchers).ConfigureAwait(false))
        {
            _developerLog.Warning("dev.hot_reload", "Dev package output was not stable yet; waiting for another file change.");
            return;
        }

        _developerLog.Info("dev.hot_reload", "Staging dev package reload.");
        using var runtimeApiClient = _runtimeApiClientFactory.CreateClient();
        var runtimeStage = await runtimeApiClient.StagePackageLifecycleAsync(new PackageLifecycleStageRequest(
            _folders
                .Select(folder => new PackageSessionLoadRequest(PackageSourceKind.Dev, folder))
                .ToArray(),
            PackageLifecycleOverlayOwner.HotReload), cancellationToken).ConfigureAwait(false);
        LogMessages(runtimeStage.Warnings, PackageLogLevel.Warning);
        LogMessages(runtimeStage.Errors, PackageLogLevel.Error);
        if (runtimeStage.StageId is null || runtimeStage.Errors.Count > 0)
        {
            await ReportFailureAsync("Runtime rejected the dev package reload.").ConfigureAwait(false);
            return;
        }

        var committed = false;
        try
        {
            await _windowLauncher.PreflightPackageLifecycleChangesAsync(
                runtimeStage.ActivePackages,
                runtimeStage.PackageSources,
                runtimeStage.ImpactedPackageIds,
                cancellationToken).ConfigureAwait(false);

            var commitResult = await runtimeApiClient.CommitPackageLifecycleStageAsync(runtimeStage.StageId, cancellationToken).ConfigureAwait(false);
            committed = commitResult.Errors.Count == 0;
            LogMessages(commitResult.Warnings, PackageLogLevel.Warning);
            LogMessages(commitResult.Errors, PackageLogLevel.Error);
            if (commitResult.Errors.Count > 0)
            {
                await ReportFailureAsync("Runtime failed to commit the dev package reload.").ConfigureAwait(false);
                return;
            }

            await _windowLauncher.ApplyPackageLifecycleChangesAsync(runtimeStage.ImpactedPackageIds, cancellationToken).ConfigureAwait(false);
            _developerLog.Info("dev.hot_reload", $"Dev packages reloaded at {DateTimeOffset.Now:T}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!committed)
            {
                await runtimeApiClient.DiscardPackageLifecycleStageAsync(runtimeStage.StageId, CancellationToken.None).ConfigureAwait(false);
            }

            _developerLog.Error("dev.hot_reload", ex.Message);
            await ReportFailureAsync("App rejected the dev package reload.").ConfigureAwait(false);
        }
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
}
