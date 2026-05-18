using System.Collections.Concurrent;
using Sunder.Protocol;
using Sunder.Sdk.Abstractions;
using SdkPackageSessionLoadRequest = Sunder.Sdk.Abstractions.PackageSessionLoadRequest;
using SdkPackageSessionSourceKind = Sunder.Sdk.Abstractions.PackageSessionSourceKind;
using SdkPackageSessionStatus = Sunder.Sdk.Abstractions.PackageSessionStatus;

namespace Sunder.App.Services;

public sealed class AppPackageSessionService(
    IRuntimeApiClientFactory runtimeApiClientFactory,
    DeveloperLogService developerLog) : IPackageSessionService, IDisposable
{
    private readonly ConcurrentDictionary<string, DevPackageSessionWatch> _watches = new(StringComparer.OrdinalIgnoreCase);
    private Func<IReadOnlyList<string>, CancellationToken, Task>? _applyPackageLifecycleChangesAsync;
    private bool _disposed;

    public void Attach(Func<IReadOnlyList<string>, CancellationToken, Task> applyPackageLifecycleChangesAsync)
        => _applyPackageLifecycleChangesAsync = applyPackageLifecycleChangesAsync;

    public void Detach(Func<IReadOnlyList<string>, CancellationToken, Task> applyPackageLifecycleChangesAsync)
    {
        if (Equals(_applyPackageLifecycleChangesAsync, applyPackageLifecycleChangesAsync))
        {
            _applyPackageLifecycleChangesAsync = null;
        }
    }

    public async Task<SdkPackageSessionStatus> LoadPackageAsync(
        SdkPackageSessionLoadRequest request,
        CancellationToken cancellationToken = default)
        => await LoadPackageCoreAsync(request, updateWatch: true, cancellationToken).ConfigureAwait(false);

    public async Task<bool> UnloadPackageAsync(
        string packageId,
        SdkPackageSessionSourceKind sourceKind,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        if (sourceKind == SdkPackageSessionSourceKind.Dev)
        {
            StopWatch(packageId);
        }

        using var runtimeApiClient = runtimeApiClientFactory.CreateClient();
        var result = await runtimeApiClient.UnloadPackageSessionAsync(
            packageId,
            ToProtocolSourceKind(sourceKind),
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return false;
        }

        await ApplyLifecycleChangesAsync(result.ImpactedPackageIds, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<SdkPackageSessionStatus?> GetPackageStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        using var runtimeApiClient = runtimeApiClientFactory.CreateClient();
        return ToSdkStatus(await runtimeApiClient.GetPackageSessionStatusAsync(packageId, cancellationToken).ConfigureAwait(false));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var watch in _watches.Values)
        {
            watch.Dispose();
        }

        _watches.Clear();
    }

    private async Task<SdkPackageSessionStatus> LoadPackageCoreAsync(
        SdkPackageSessionLoadRequest request,
        bool updateWatch,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        using var runtimeApiClient = runtimeApiClientFactory.CreateClient();
        var result = await runtimeApiClient.LoadPackageSessionAsync(
            new Sunder.Protocol.PackageSessionLoadRequest(
                ToProtocolSourceKind(request.SourceKind),
                request.Source,
                request.Watch),
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            var message = result.Errors.FirstOrDefault() ?? result.Message ?? "Package session load failed.";
            throw new InvalidOperationException(message);
        }

        await ApplyLifecycleChangesAsync(result.ImpactedPackageIds, cancellationToken).ConfigureAwait(false);
        var status = ToSdkStatus(result.Status)
            ?? throw new InvalidOperationException("Runtime did not return package session status after loading the package.");

        if (updateWatch && request.SourceKind == SdkPackageSessionSourceKind.Dev)
        {
            if (request.Watch)
            {
                StartWatch(status.PackageId, request.Source);
            }
            else
            {
                StopWatch(status.PackageId);
            }
        }

        return status;
    }

    private async Task ApplyLifecycleChangesAsync(
        IReadOnlyList<string> impactedPackageIds,
        CancellationToken cancellationToken)
    {
        if (impactedPackageIds.Count == 0)
        {
            return;
        }

        var applyPackageLifecycleChangesAsync = _applyPackageLifecycleChangesAsync
            ?? throw new InvalidOperationException("Package session service is not attached to the running shell yet.");
        await applyPackageLifecycleChangesAsync(impactedPackageIds, cancellationToken).ConfigureAwait(false);
    }

    private void StartWatch(string packageId, string folder)
    {
        StopWatch(packageId);
        var watch = new DevPackageSessionWatch(
            packageId,
            Path.GetFullPath(folder),
            ReloadWatchedDevPackageAsync,
            developerLog);
        if (!_watches.TryAdd(packageId, watch))
        {
            watch.Dispose();
            return;
        }

        watch.Start();
    }

    private void StopWatch(string packageId)
    {
        if (_watches.TryRemove(packageId, out var watch))
        {
            watch.Dispose();
        }
    }

    private async Task ReloadWatchedDevPackageAsync(string packageId, string folder, CancellationToken cancellationToken)
    {
        try
        {
            await LoadPackageCoreAsync(
                new SdkPackageSessionLoadRequest(SdkPackageSessionSourceKind.Dev, folder, Watch: true),
                updateWatch: false,
                cancellationToken).ConfigureAwait(false);
            developerLog.Info("package.session", $"Reloaded dev package '{packageId}'.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            developerLog.Error("package.session", $"Failed to reload dev package '{packageId}': {ex.Message}");
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private static PackageSourceKind ToProtocolSourceKind(SdkPackageSessionSourceKind sourceKind)
        => sourceKind switch
        {
            SdkPackageSessionSourceKind.Installed => PackageSourceKind.Installed,
            SdkPackageSessionSourceKind.Dev => PackageSourceKind.Dev,
            _ => throw new ArgumentOutOfRangeException(nameof(sourceKind), sourceKind, null),
        };

    private static SdkPackageSessionSourceKind ToSdkSourceKind(PackageSourceKind sourceKind)
        => sourceKind switch
        {
            PackageSourceKind.Installed => SdkPackageSessionSourceKind.Installed,
            PackageSourceKind.Dev => SdkPackageSessionSourceKind.Dev,
            _ => SdkPackageSessionSourceKind.Installed,
        };

    private static SdkPackageSessionStatus? ToSdkStatus(Sunder.Protocol.PackageSessionStatus? status)
        => status is null
            ? null
            : new SdkPackageSessionStatus(
                status.PackageId,
                status.DisplayName,
                status.Version,
                ToSdkSourceKind(status.ActiveSourceKind),
                status.IsLoaded,
                status.WatchEnabled,
                status.OverridesInstalledPackage,
                status.ErrorMessage);

    private sealed class DevPackageSessionWatch(
        string packageId,
        string folder,
        Func<string, string, CancellationToken, Task> reloadAsync,
        DeveloperLogService developerLog) : IDisposable
    {
        private readonly object _gate = new();
        private FileSystemWatcher? _folderWatcher;
        private FileSystemWatcher? _parentWatcher;
        private CancellationTokenSource? _pendingReload;
        private bool _disposed;

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _folderWatcher = CreateFolderWatcher(folder);
                _parentWatcher = CreateParentWatcher(folder);
            }

            developerLog.Info("package.session", $"Watching dev package '{packageId}' at {folder}.");
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _pendingReload?.Cancel();
                _pendingReload?.Dispose();
                _folderWatcher?.Dispose();
                _parentWatcher?.Dispose();
            }
        }

        private FileSystemWatcher? CreateFolderWatcher(string path)
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            Attach(watcher);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }

        private FileSystemWatcher? CreateParentWatcher(string path)
        {
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            {
                return null;
            }

            var watcher = new FileSystemWatcher(parent)
            {
                IncludeSubdirectories = false,
                Filter = Path.GetFileName(path),
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            Attach(watcher);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }

        private void Attach(FileSystemWatcher watcher)
        {
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (DevPackageWatchSupport.ShouldIgnorePath(e.FullPath))
            {
                return;
            }

            QueueReload();
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            if (DevPackageWatchSupport.ShouldIgnorePath(e.FullPath) && DevPackageWatchSupport.ShouldIgnorePath(e.OldFullPath))
            {
                return;
            }

            QueueReload();
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            developerLog.Warning("package.session", $"Dev package watcher for '{packageId}' reported an error: {e.GetException().Message}");
            QueueReload();
        }

        private void QueueReload()
        {
            CancellationTokenSource reload;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _pendingReload?.Cancel();
                _pendingReload?.Dispose();
                _pendingReload = new CancellationTokenSource();
                reload = _pendingReload;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(DevPackageWatchSupport.DebounceDelay, reload.Token).ConfigureAwait(false);
                    RefreshFolderWatcher();
                    if (!await DevPackageWatchSupport.WaitForStableFoldersAsync(
                            [folder],
                            Task.Delay,
                            reload.Token,
                            requireLibraryFolder: true,
                            onLoadabilityRetry: RefreshFolderWatcher).ConfigureAwait(false))
                    {
                        developerLog.Warning("package.session", $"Dev package '{packageId}' output was not stable yet; waiting for another file change.");
                        return;
                    }

                    RefreshFolderWatcher();
                    await reloadAsync(packageId, folder, reload.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (reload.IsCancellationRequested)
                {
                }
            });
        }

        private void RefreshFolderWatcher()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (!Directory.Exists(folder))
                {
                    _folderWatcher?.Dispose();
                    _folderWatcher = null;
                    return;
                }

                _folderWatcher ??= CreateFolderWatcher(folder);
            }
        }
    }
}
