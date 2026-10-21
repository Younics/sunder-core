using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class DevPackageWatchService : IAsyncDisposable
{
    internal static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(750);
    internal static readonly TimeSpan StabilityProbeDelay = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan MaxStabilityWait = TimeSpan.FromSeconds(5);
    // The generated-output V1 journal fences the directory and its sibling handoff.
    private const string OutputTransactionSuffix = ".sunder-output-transaction.json";

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
    private readonly HashSet<string> _completedPublications = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _reloadCancellation;
    private Task _reloadTask = Task.CompletedTask;
    private bool _enabled;
    private bool _disposed;

    public DevPackageWatchService(
        PackageSessionLifecycleService packageSessionService,
        RuntimeEventStreamService eventStream,
        ILogger<DevPackageWatchService> logger)
        : this(
            packageSessionService.StageWatchedAsync,
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
        var synchronizationStarted = Stopwatch.GetTimestamp();
        var gateWaitStarted = Stopwatch.GetTimestamp();
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var gateWaitMilliseconds = Stopwatch.GetElapsedTime(gateWaitStarted).TotalMilliseconds;
        var synchronized = false;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _enabled = targets.Count > 0;
                var withdrawn = ReplaceRegistrations(targets);
                var superseded = withdrawn.Any(_changedPackageIds.Contains);
                foreach (var packageId in withdrawn)
                {
                    _changedPackageIds.Remove(packageId);
                    _completedPublications.Remove(packageId);
                }
                if (!_enabled)
                {
                    _reloadCancellation?.Cancel();
                    _changedPackageIds.Clear();
                    _completedPublications.Clear();
                }
                else if (superseded)
                {
                    // Disabling/rebinding one owner also revokes its pending
                    // staging work, even while unrelated registrations survive.
                    // A late candidate is discarded before commit. Keep any
                    // still-owned dirty packages queued under the new graph.
                    if (_changedPackageIds.Count > 0) StartReloadLocked();
                    else _reloadCancellation?.Cancel();
                }
            }
            synchronized = true;
        }
        finally
        {
            _transitionGate.Release();
            if (synchronized)
            {
                _logger.LogInformation(
                    "Synchronized {WatchTargetCount} dev package watcher target(s) in {ElapsedMilliseconds} ms after waiting {GateWaitMilliseconds} ms for the watcher transition gate",
                    targets.Count,
                    Stopwatch.GetElapsedTime(synchronizationStarted).TotalMilliseconds,
                    gateWaitMilliseconds);
            }
            else
            {
                _logger.LogWarning(
                    "Dev package watcher synchronization for {WatchTargetCount} target(s) failed after {ElapsedMilliseconds} ms and a {GateWaitMilliseconds} ms transition gate wait",
                    targets.Count,
                    Stopwatch.GetElapsedTime(synchronizationStarted).TotalMilliseconds,
                    gateWaitMilliseconds);
            }
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
                _completedPublications.Clear();
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

    private HashSet<string> ReplaceRegistrations(IReadOnlyList<DevPackageWatchTarget> targets)
    {
        var withdrawn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in _registrations.Keys.Except(targets.Select(target => target.PackageId), StringComparer.OrdinalIgnoreCase).ToArray())
        {
            withdrawn.Add(packageId);
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

            if (existing is not null) withdrawn.Add(target.PackageId);
            existing?.Dispose();
            _registrations[target.PackageId] = new WatchRegistration(
                target.PackageId,
                target.Folder,
                ScheduleReload);
        }
        return withdrawn;
    }

    private void ScheduleReload(string packageId, bool refreshWatcher, bool publicationCompleted)
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

            var publicationInProgress = IsPublicationInProgress(registration.Folder);
            // Parent and recursive watchers can deliver the same transaction's
            // notifications after journal removal, including after strict staging
            // has started. Coalesce an unchanged filesystem observation instead of
            // canceling that candidate and repeatedly restarting its publication.
            // These stamps are scheduling evidence only; every candidate still
            // goes through strict canonical/content/import validation.
            if (!publicationInProgress && !refreshWatcher && registration.CompletedPublicationFiles is { } completedFiles
                && TrySnapshotFiles([registration], out var currentFiles) && SnapshotsEqual(completedFiles, currentFiles))
            {
                _logger.LogDebug("Coalesced delayed notification for completed dev publication {PackageId}", packageId);
                return;
            }

            _changedPackageIds.Add(packageId);
            if (publicationCompleted && !publicationInProgress)
            {
                _completedPublications.Add(packageId);
                registration.CompletedPublicationFiles = TrySnapshotFiles([registration], out var files) ? files : null;
            }
            else if (publicationInProgress || refreshWatcher || registration.CompletedPublicationFiles is not null)
            {
                _completedPublications.Remove(packageId);
                registration.CompletedPublicationFiles = null;
            }
            // Without a new journal or changed observation, a delayed child event
            // must not erase an already observed completion boundary.
            _logger.LogDebug("Scheduled dev watch reload for {PackageId}: publication completed {PublicationCompleted}, publication in progress {PublicationInProgress}, rebind {RefreshWatcher}",
                packageId, publicationCompleted, publicationInProgress, refreshWatcher);
            StartReloadLocked();
        }
    }

    private void StartReloadLocked()
    {
        _reloadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        var previousReload = _reloadTask;
        _reloadCancellation = cancellation;
        _reloadTask = RunSerializedReloadAsync(previousReload, cancellation,
            _changedPackageIds.All(_completedPublications.Contains), Stopwatch.GetTimestamp());
        _eventStream.PublishOperationPhase(RuntimeOperationPhase.DevReloadDebounce, _changedPackageIds.ToArray());
    }

    private async Task RunSerializedReloadAsync(Task previousReload, CancellationTokenSource cancellation,
        bool publicationCompleted, long scheduledAt)
    {
        try
        {
            await previousReload.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await DebounceAndReloadAsync(cancellation, publicationCompleted, scheduledAt).ConfigureAwait(false);
    }

    private async Task DebounceAndReloadAsync(CancellationTokenSource cancellation, bool publicationCompleted, long scheduledAt)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            // A completed directory+handoff transaction already supplies the write
            // boundary. The strict staging path still validates the full manifest,
            // indexed bytes, canonical shadow and import authority before commit.
            // Ordinary in-place edits keep the debounce/stability fallback.
            if (publicationCompleted) await Task.Yield();
            else await _delayAsync(DebounceDelay, cancellationToken).ConfigureAwait(false);
            WatchRegistration[] registrations;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_reloadCancellation, cancellation))
                {
                    return;
                }

                var changedIds = _changedPackageIds.ToArray();
                // Keep the entire dirty set until this attempt finishes. A new
                // event may cancel staging after this point; its successor must
                // include every package from the interrupted attempt.
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
            if (!(publicationCompleted && registrations.All(registration => IsLoadableDevPackageFolder(registration.Folder)))
                && !await WaitForStableFoldersAsync(registrations, cancellationToken).ConfigureAwait(false))
            {
                const string message = "Dev package output did not become stable before the reload deadline.";
                _eventStream.PublishDevReloadResult(false, packageIds, message);
                _logger.LogWarning("{Message} Packages: {PackageIds}", message, packageIds);
                return;
            }

            _eventStream.PublishOperationPhase(RuntimeOperationPhase.DevReloading, packageIds);
            var stageStarted = Stopwatch.GetTimestamp();
            var stage = await _stageAsync(
                new PackageLifecycleStageRequest(packageIds),
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Dev package watch strict staging completed in {ElapsedMilliseconds} ms", Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds);
            if (stage.StageId is null || stage.Errors.Count > 0)
            {
                _eventStream.PublishDevReloadResult(false, packageIds, stage.Errors.FirstOrDefault() ?? "Runtime rejected the dev package reload.");
                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (registrations.Any(registration => !IsLoadableDevPackageFolder(registration.Folder)))
                    throw new IOException("Dev package output publication changed before reload commit.");
                var result = await _commitAsync(stage.StageId, cancellationToken).ConfigureAwait(false);
                // An owner can withdraw/rebind while the exact commit response is
                // returning. Its late result no longer describes the current watch
                // attempt. Drain/discard that stage through the cancellation path;
                // an actual committed Runtime generation publishes its own snapshot.
                cancellationToken.ThrowIfCancellationRequested();
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
            _logger.LogDebug("Dev package watch reload was superseded after {ElapsedMilliseconds} ms", Stopwatch.GetElapsedTime(scheduledAt).TotalMilliseconds);
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
                    _changedPackageIds.Clear();
                    _completedPublications.Clear();
                    _eventStream.PublishOperationPhase(RuntimeOperationPhase.Idle);
                    _logger.LogInformation(
                        "Dev package watch reload using {SchedulingMode} scheduling completed in {ElapsedMilliseconds} ms",
                        publicationCompleted ? "transaction" : "stability",
                        Stopwatch.GetElapsedTime(scheduledAt).TotalMilliseconds);
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
           && !IsPublicationInProgress(folder)
           && File.Exists(Path.Combine(folder, "manifest", "sunder-package.json"))
           && File.Exists(Path.Combine(folder, "manifest", "content-index.json"))
           && Directory.Exists(Path.Combine(folder, "payload"));

    internal static bool IsPublicationInProgress(string folder)
        => File.Exists(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + OutputTransactionSuffix);

    private static bool TrySnapshotFiles(
        IReadOnlyList<WatchRegistration> registrations,
        out Dictionary<string, FileSnapshot> snapshot)
    {
        snapshot = new Dictionary<string, FileSnapshot>(PathComparer);
        try
        {
            foreach (var registration in registrations)
            {
                if (!IsLoadableDevPackageFolder(registration.Folder)) return false;
                foreach (var filePath in Directory.EnumerateFiles(registration.Folder, "*", SearchOption.AllDirectories))
                {
                    if (ShouldIgnorePath(filePath))
                    {
                        continue;
                    }

                    var info = new FileInfo(filePath);
                    snapshot[filePath] = new FileSnapshot(info.Length, info.LastWriteTimeUtc);
                }

                var imports = new FileInfo(registration.Folder + SunderPackageFormat.DevelopmentImportsSuffix);
                snapshot[imports.FullName] = imports.Exists
                    ? new FileSnapshot(imports.Length, imports.LastWriteTimeUtc)
                    : new FileSnapshot(-1, default);
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
        private readonly Action<string, bool, bool> _onChanged;
        private readonly object _syncRoot = new();
        private FileSystemWatcher? _folderWatcher;
        private readonly FileSystemWatcher? _parentWatcher;
        private bool _disposed;

        public WatchRegistration(string packageId, string folder, Action<string, bool, bool> onChanged)
        {
            PackageId = packageId;
            Folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
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

        // Accessed only while the owning service's scheduling gate is held.
        public Dictionary<string, FileSnapshot>? CompletedPublicationFiles { get; set; }

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
            watcher.Changed += OnChanged;
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
            if (ReferenceEquals(sender, _parentWatcher) && !IsPublicationPath(e.FullPath))
            {
                return;
            }

            if (!ShouldIgnorePath(e.FullPath))
            {
                // A directory Changed event does not mean its watcher binding was
                // replaced. Recreating it here can replay native events and turn a
                // single publication into a self-sustaining cancellation storm.
                _onChanged(PackageId, ReferenceEquals(sender, _parentWatcher) && IsTargetFolder(e.FullPath)
                        && e.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Deleted,
                    ReferenceEquals(sender, _parentWatcher) && IsJournalPath(e.FullPath) && !IsPublicationInProgress(Folder));
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
                && !IsPublicationPath(e.FullPath)
                && !IsPublicationPath(e.OldFullPath))
            {
                return;
            }

            if (!ShouldIgnorePath(e.FullPath) || !ShouldIgnorePath(e.OldFullPath))
            {
                _onChanged(PackageId, ReferenceEquals(sender, _parentWatcher)
                    && (IsTargetFolder(e.FullPath) || IsTargetFolder(e.OldFullPath)),
                    ReferenceEquals(sender, _parentWatcher) && IsJournalPath(e.OldFullPath) && !IsPublicationInProgress(Folder));
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
            _onChanged(PackageId, true, false);
        }

        private bool IsTargetFolder(string path)
            => string.Equals(Path.GetFullPath(path), Folder, PathComparison);

        private bool IsJournalPath(string path)
            => string.Equals(Path.GetFullPath(path), Folder + OutputTransactionSuffix, PathComparison);

        private bool IsPublicationPath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            return IsTargetFolder(fullPath)
                || string.Equals(fullPath, Folder + SunderPackageFormat.DevelopmentImportsSuffix, PathComparison)
                || string.Equals(fullPath, Folder + OutputTransactionSuffix, PathComparison);
        }
    }
}

internal sealed record DevPackageWatchTarget(string PackageId, string Folder);
