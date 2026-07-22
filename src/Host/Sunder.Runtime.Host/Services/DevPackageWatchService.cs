using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class DevPackageWatchService : IAsyncDisposable
{
    internal static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(750);
    internal static readonly TimeSpan StabilityProbeDelay = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan MaxStabilityWait = TimeSpan.FromSeconds(5);

    private readonly Func<PackageLifecycleStageRequest, CancellationToken, Task<PackageLifecycleStageResult>> _stageAsync;
    private readonly Func<string, CancellationToken, Task<PackageLifecycleOperationResult>> _commitAsync;
    private readonly Func<string, CancellationToken, Task<bool>> _discardAsync;
    private readonly RuntimeEventStreamService _eventStream;
    private readonly ILogger<DevPackageWatchService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, WatchRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _changedPackageIds = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _reloadCancellation;
    private Task _reloadTask = Task.CompletedTask;
    private bool _enabled;
    private bool _disposed;

    public DevPackageWatchService(
        PackageSessionLifecycleService packageSessionService,
        RuntimeEventStreamService eventStream,
        ILogger<DevPackageWatchService> logger)
        : this(
            packageSessionService.StageAsync,
            packageSessionService.CommitStageAsync,
            packageSessionService.DiscardStageAsync,
            eventStream,
            logger,
            Task.Delay)
    {
    }

    internal DevPackageWatchService(
        Func<PackageLifecycleStageRequest, CancellationToken, Task<PackageLifecycleStageResult>> stageAsync,
        Func<string, CancellationToken, Task<PackageLifecycleOperationResult>> commitAsync,
        Func<string, CancellationToken, Task<bool>> discardAsync,
        RuntimeEventStreamService eventStream,
        ILogger<DevPackageWatchService> logger,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _stageAsync = stageAsync;
        _commitAsync = commitAsync;
        _discardAsync = discardAsync;
        _eventStream = eventStream;
        _logger = logger;
        _delayAsync = delayAsync;
    }

    public async Task SynchronizeAsync(
        IReadOnlyList<DevPackageWatchTarget> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _enabled = targets.Count > 0;
                ReplaceRegistrations(targets);
                if (!_enabled)
                {
                    _reloadCancellation?.Cancel();
                    _changedPackageIds.Clear();
                }
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Task reloadTask;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _enabled = false;
                _reloadCancellation?.Cancel();
                foreach (var registration in _registrations.Values)
                {
                    registration.Dispose();
                }

                _registrations.Clear();
                _changedPackageIds.Clear();
                reloadTask = _reloadTask;
            }

            try
            {
                await reloadTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            lock (_gate)
            {
                _reloadCancellation?.Dispose();
                _reloadCancellation = null;
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private void ReplaceRegistrations(IReadOnlyList<DevPackageWatchTarget> targets)
    {
        foreach (var packageId in _registrations.Keys.Except(targets.Select(target => target.PackageId), StringComparer.OrdinalIgnoreCase).ToArray())
        {
            _registrations.Remove(packageId, out var registration);
            registration?.Dispose();
        }

        foreach (var target in targets)
        {
            if (_registrations.TryGetValue(target.PackageId, out var existing)
                && string.Equals(existing.Folder, target.Folder, PathComparison))
            {
                continue;
            }

            existing?.Dispose();
            _registrations[target.PackageId] = new WatchRegistration(
                target.PackageId,
                target.Folder,
                ScheduleReload);
        }
    }

    private void ScheduleReload(string packageId, bool refreshWatcher)
    {
        lock (_gate)
        {
            if (_disposed || !_enabled || !_registrations.TryGetValue(packageId, out var registration))
            {
                return;
            }

            if (refreshWatcher)
            {
                registration.RefreshFolderWatcher(recreate: true);
            }

            _changedPackageIds.Add(packageId);
            _reloadCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            var previousReload = _reloadTask;
            _reloadCancellation = cancellation;
            _reloadTask = RunSerializedReloadAsync(previousReload, cancellation);
            _eventStream.PublishOperationPhase(RuntimeOperationPhase.DevReloadDebounce, _changedPackageIds.ToArray());
        }
    }

    private async Task RunSerializedReloadAsync(Task previousReload, CancellationTokenSource cancellation)
    {
        try
        {
            await previousReload.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await DebounceAndReloadAsync(cancellation).ConfigureAwait(false);
    }

    private async Task DebounceAndReloadAsync(CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            await _delayAsync(DebounceDelay, cancellationToken).ConfigureAwait(false);
            WatchRegistration[] registrations;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_reloadCancellation, cancellation))
                {
                    return;
                }

                var changedIds = _changedPackageIds.ToArray();
                _changedPackageIds.Clear();
                registrations = changedIds
                    .Select(packageId => _registrations.GetValueOrDefault(packageId))
                    .Where(registration => registration is not null)
                    .Select(registration => registration!)
                    .ToArray();
            }

            if (registrations.Length == 0)
            {
                return;
            }

            var packageIds = registrations.Select(registration => registration.PackageId).ToArray();
            _eventStream.PublishOperationPhase(RuntimeOperationPhase.DevReloadStabilityCheck, packageIds);
            if (!await WaitForStableFoldersAsync(registrations, cancellationToken).ConfigureAwait(false))
            {
                const string message = "Dev package output did not become stable before the reload deadline.";
                _eventStream.PublishDevReloadResult(false, packageIds, message);
                _logger.LogWarning("{Message} Packages: {PackageIds}", message, packageIds);
                return;
            }

            _eventStream.PublishOperationPhase(RuntimeOperationPhase.DevReloading, packageIds);
            var stage = await _stageAsync(
                new PackageLifecycleStageRequest(packageIds),
                cancellationToken).ConfigureAwait(false);
            if (stage.StageId is null || stage.Errors.Count > 0)
            {
                _eventStream.PublishDevReloadResult(false, packageIds, stage.Errors.FirstOrDefault() ?? "Runtime rejected the dev package reload.");
                return;
            }

            try
            {
                var result = await _commitAsync(stage.StageId, cancellationToken).ConfigureAwait(false);
                _eventStream.PublishDevReloadResult(
                    result.Success && result.Errors.Count == 0,
                    result.ImpactedPackageIds.Count == 0 ? packageIds : result.ImpactedPackageIds,
                    result.Errors.FirstOrDefault() ?? result.Message);
            }
            catch
            {
                await _discardAsync(stage.StageId, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dev package reload failed");
            _eventStream.PublishDevReloadResult(false, [], ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_reloadCancellation, cancellation))
                {
                    _reloadCancellation = null;
                    _eventStream.PublishOperationPhase(RuntimeOperationPhase.Idle);
                }
            }

            cancellation.Dispose();
        }
    }

    private async Task<bool> WaitForStableFoldersAsync(
        IReadOnlyList<WatchRegistration> registrations,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + MaxStabilityWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var registration in registrations)
            {
                registration.RefreshFolderWatcher();
            }

            if (!registrations.All(registration => IsLoadableDevPackageFolder(registration.Folder))
                || !TrySnapshotFiles(registrations, out var before))
            {
                await _delayAsync(StabilityProbeDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await _delayAsync(StabilityProbeDelay, cancellationToken).ConfigureAwait(false);
            if (TrySnapshotFiles(registrations, out var after) && SnapshotsEqual(before, after))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLoadableDevPackageFolder(string folder)
        => Directory.Exists(folder)
           && File.Exists(Path.Combine(folder, "sunder-package.json"))
           && Directory.Exists(Path.Combine(folder, "lib"));

    private static bool TrySnapshotFiles(
        IReadOnlyList<WatchRegistration> registrations,
        out Dictionary<string, FileSnapshot> snapshot)
    {
        snapshot = new Dictionary<string, FileSnapshot>(PathComparer);
        try
        {
            foreach (var registration in registrations)
            {
                foreach (var filePath in Directory.EnumerateFiles(registration.Folder, "*", SearchOption.AllDirectories))
                {
                    if (ShouldIgnorePath(filePath))
                    {
                        continue;
                    }

                    var info = new FileInfo(filePath);
                    snapshot[filePath] = new FileSnapshot(info.Length, info.LastWriteTimeUtc);
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

    private static bool SnapshotsEqual(
        IReadOnlyDictionary<string, FileSnapshot> left,
        IReadOnlyDictionary<string, FileSnapshot> right)
        => left.Count == right.Count
           && left.All(entry => right.TryGetValue(entry.Key, out var value) && value == entry.Value);

    private static bool ShouldIgnorePath(string path)
    {
        var fileName = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName)
               || string.Equals(fileName, ".DS_Store", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".swp", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly record struct FileSnapshot(long Length, DateTime LastWriteTimeUtc);

    private sealed class WatchRegistration : IDisposable
    {
        private readonly Action<string, bool> _onChanged;
        private readonly object _syncRoot = new();
        private FileSystemWatcher? _folderWatcher;
        private readonly FileSystemWatcher? _parentWatcher;
        private bool _disposed;

        public WatchRegistration(string packageId, string folder, Action<string, bool> onChanged)
        {
            PackageId = packageId;
            Folder = Path.GetFullPath(folder);
            _onChanged = onChanged;
            _folderWatcher = CreateFolderWatcher();
            var parent = Path.GetDirectoryName(Folder);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            {
                _parentWatcher = CreateWatcher(parent, includeSubdirectories: false, filter: null);
            }
        }

        public string PackageId { get; }

        public string Folder { get; }

        public void RefreshFolderWatcher(bool recreate = false)
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                if (recreate || !Directory.Exists(Folder))
                {
                    _folderWatcher?.Dispose();
                    _folderWatcher = null;
                    if (!Directory.Exists(Folder))
                    {
                        return;
                    }
                }

                _folderWatcher ??= CreateFolderWatcher();
            }
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
                _folderWatcher?.Dispose();
                _folderWatcher = null;
                _parentWatcher?.Dispose();
            }
        }

        private FileSystemWatcher? CreateFolderWatcher()
            => Directory.Exists(Folder) ? CreateWatcher(Folder, includeSubdirectories: true, filter: null) : null;

        private FileSystemWatcher? CreateWatcher(string path, bool includeSubdirectories, string? filter)
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = includeSubdirectories,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size
                               | NotifyFilters.CreationTime,
                Filter = filter ?? "*",
            };
            if (includeSubdirectories)
            {
                watcher.Changed += OnChanged;
            }
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            try
            {
                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch (Exception exception) when (exception is DirectoryNotFoundException or IOException or ArgumentException)
            {
                watcher.Dispose();
                return null;
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }
            }
            if (ReferenceEquals(sender, _parentWatcher) && !IsTargetFolder(e.FullPath))
            {
                return;
            }

            if (!ShouldIgnorePath(e.FullPath))
            {
                _onChanged(PackageId, ReferenceEquals(sender, _parentWatcher));
            }
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }
            }
            if (ReferenceEquals(sender, _parentWatcher)
                && !IsTargetFolder(e.FullPath)
                && !IsTargetFolder(e.OldFullPath))
            {
                return;
            }

            if (!ShouldIgnorePath(e.FullPath) || !ShouldIgnorePath(e.OldFullPath))
            {
                _onChanged(PackageId, ReferenceEquals(sender, _parentWatcher));
            }
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }
            }
            _onChanged(PackageId, true);
        }

        private bool IsTargetFolder(string path)
            => string.Equals(Path.GetFullPath(path), Folder, PathComparison);
    }
}

internal sealed record DevPackageWatchTarget(string PackageId, string Folder);
