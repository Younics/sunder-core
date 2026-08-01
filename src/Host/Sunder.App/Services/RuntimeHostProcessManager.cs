using System.Diagnostics;
using Sunder.App.Models;
using Sunder.Host.Client;
using Sunder.Host.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed class RuntimeHostProcessManager : IDisposable
{
    private const string RuntimeHostName = "Sunder.Host.Supervisor";
    internal static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(400);
    private readonly AppStartupOptions _startupOptions;
    private readonly RuntimeConnectionState _runtimeConnectionState;
    private readonly Func<string?> _resolveRuntimeHostPath;
    private readonly Func<Uri, CancellationToken, Task<RuntimeHandshakeResponse?>> _tryGetRuntimeHandshakeAsync;
    private readonly Func<Uri, CancellationToken, Task<bool>> _isRuntimeHealthyAsync;
    private readonly Func<Uri, CancellationToken, Task> _shutdownRuntimeAsync;
    private readonly Func<ProcessStartInfo, bool, CancellationToken, Task> _launchRuntimeAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly string _connectionInfoPath;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _startupTimeout;
    private readonly Func<bool> _isRuntimeLeaseAvailable;
    private readonly Func<Uri, CancellationToken, Task<HostHandshakeResponse?>> _tryGetHostHandshakeAsync;
    private readonly Func<Uri, CancellationToken, Task<bool>> _tryEnsureSupervisedRuntimeStartedAsync;
    private readonly Func<bool> _isHostInstanceLockAvailable;
    private readonly UserHostPayloadStore? _userHostPayloadStore;
    private readonly bool _restartManagedHostOnFirstStart;
    private readonly SemaphoreSlim _startupSemaphore = new(1, 1);
    private readonly RuntimeHealthProbe? _healthProbe;
    private bool _managedHostRestartCompleted;

    public RuntimeHostProcessManager(AppStartupOptions startupOptions)
        : this(startupOptions, null, null, null, null, null, null, null, null)
    {
    }

    public RuntimeHostProcessManager(
        AppStartupOptions startupOptions,
        RuntimeConnectionState runtimeConnectionState)
        : this(startupOptions, runtimeConnectionState, null, null, null, null, null, null, null, null)
    {
    }

    public RuntimeHostProcessManager(
        AppStartupOptions startupOptions,
        RuntimeConnectionState runtimeConnectionState,
        RuntimeClientTransport runtimeTransport)
        : this(startupOptions, runtimeConnectionState, null, null, null, null, null, null, null, runtimeTransport)
    {
    }

    internal RuntimeHostProcessManager(
        AppStartupOptions startupOptions,
        RuntimeConnectionState? runtimeConnectionState = null,
        Func<string?>? resolveRuntimeHostPath = null,
        Func<Uri, CancellationToken, Task<RuntimeHandshakeResponse?>>? tryGetRuntimeHandshakeAsync = null,
        Func<Uri, CancellationToken, Task<bool>>? isRuntimeHealthyAsync = null,
        Func<Uri, CancellationToken, Task>? shutdownRuntimeAsync = null,
        Action<ProcessStartInfo>? startProcess = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        string? connectionInfoPath = null,
        RuntimeClientTransport? runtimeTransport = null,
        TimeProvider? timeProvider = null,
        TimeSpan? startupTimeout = null,
        Func<bool>? isRuntimeLeaseAvailable = null,
        Func<Uri, CancellationToken, Task<HostHandshakeResponse?>>? tryGetHostHandshakeAsync = null,
        UserHostPayloadStore? userHostPayloadStore = null,
        Func<Uri, CancellationToken, Task<bool>>? tryEnsureSupervisedRuntimeStartedAsync = null,
        Func<bool>? isHostInstanceLockAvailable = null,
        bool? restartManagedHostOnFirstStart = null)
    {
        _startupOptions = startupOptions;
        _runtimeConnectionState = runtimeConnectionState ?? new RuntimeConnectionState(startupOptions.RuntimeUrl);
        var healthProbe = tryGetRuntimeHandshakeAsync is null
                          || isRuntimeHealthyAsync is null
                          || shutdownRuntimeAsync is null
            ? new RuntimeHealthProbe(_runtimeConnectionState, runtimeTransport)
            : null;
        _healthProbe = healthProbe;
        _resolveRuntimeHostPath = resolveRuntimeHostPath ?? ResolveRuntimeHostPath;
        _tryGetRuntimeHandshakeAsync = tryGetRuntimeHandshakeAsync ?? healthProbe!.TryGetRuntimeHandshakeAsync;
        _isRuntimeHealthyAsync = isRuntimeHealthyAsync ?? healthProbe!.IsRuntimeHealthyAsync;
        _shutdownRuntimeAsync = shutdownRuntimeAsync ?? healthProbe!.ShutdownRuntimeAsync;
        if (tryGetHostHandshakeAsync is not null)
        {
            _tryGetHostHandshakeAsync = tryGetHostHandshakeAsync;
        }
        else if (healthProbe is not null)
        {
            _tryGetHostHandshakeAsync = healthProbe.TryGetHostHandshakeAsync;
        }
        else
        {
            _tryGetHostHandshakeAsync = static (_, _) => Task.FromResult<HostHandshakeResponse?>(null);
        }
        if (tryEnsureSupervisedRuntimeStartedAsync is not null)
        {
            _tryEnsureSupervisedRuntimeStartedAsync = tryEnsureSupervisedRuntimeStartedAsync;
        }
        else if (healthProbe is not null)
        {
            _tryEnsureSupervisedRuntimeStartedAsync = healthProbe.TryEnsureSupervisedRuntimeStartedAsync;
        }
        else
        {
            _tryEnsureSupervisedRuntimeStartedAsync = static (_, _) => Task.FromResult(false);
        }
        var persistentLauncher = RuntimePersistentLauncher.Create();
        _launchRuntimeAsync = startProcess is null
            ? persistentLauncher.LaunchAsync
            : (startInfo, _, _) =>
            {
                startProcess(startInfo);
                return Task.CompletedTask;
            };
        _delayAsync = delayAsync ?? Task.Delay;
        _connectionInfoPath = connectionInfoPath ?? HostConnectionInfoStore.GetDefaultPath();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startupTimeout = startupTimeout ?? DefaultStartupTimeout;
        _isRuntimeLeaseAvailable = isRuntimeLeaseAvailable ?? (() => RuntimeLocalState.IsLeaseAvailable());
#if DEBUG
        _userHostPayloadStore = userHostPayloadStore;
#else
        _userHostPayloadStore = resolveRuntimeHostPath is null
                                && string.IsNullOrWhiteSpace(startupOptions.RuntimeHostPath)
            ? userHostPayloadStore ?? new UserHostPayloadStore()
            : userHostPayloadStore;
#endif
        _restartManagedHostOnFirstStart = restartManagedHostOnFirstStart
            ?? ShouldRestartManagedHostForDefaultDevelopmentLaunch(
                resolveRuntimeHostPath,
                tryGetRuntimeHandshakeAsync,
                isRuntimeHealthyAsync,
                shutdownRuntimeAsync,
                startProcess,
                userHostPayloadStore);
        _isHostInstanceLockAvailable = isHostInstanceLockAvailable
            ?? (connectionInfoPath is null
                ? HostConnectionInfoStore.IsLifecycleLockAvailable
                : static () => true);
        if (_startupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startupTimeout), "Runtime startup timeout must be positive.");
        }
    }

    public void Dispose()
    {
        _healthProbe?.Dispose();
        _startupSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
        => await EnsureStartedAsync(_startupOptions.RuntimeUrl, cancellationToken);

    public async Task EnsureStartedAsync(Uri runtimeUrl, CancellationToken cancellationToken = default)
    {
        runtimeUrl = RuntimeUrlHelper.Normalize(runtimeUrl);
        _runtimeConnectionState.RuntimeUrl = runtimeUrl;
        await _startupSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStartedCoreAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startupSemaphore.Release();
        }
    }

    public async Task StopAsync(Uri runtimeUrl, CancellationToken cancellationToken = default)
    {
        runtimeUrl = RuntimeUrlHelper.Normalize(runtimeUrl);
        _runtimeConnectionState.RuntimeUrl = runtimeUrl;
        RefreshPublishedConnection(runtimeUrl);
        if (_healthProbe is not null
            && await _healthProbe.TryStopSupervisedRuntimeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        await _shutdownRuntimeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureStartedCoreAsync(Uri runtimeUrl, CancellationToken cancellationToken)
    {
        var developmentRestartPending = _restartManagedHostOnFirstStart
                                        && !_managedHostRestartCompleted
                                        && runtimeUrl.IsLoopback;
        using var payload = runtimeUrl.IsLoopback ? _userHostPayloadStore?.Prepare() : null;
        var runtimeHostPath = payload?.ExecutablePath ?? _resolveRuntimeHostPath();
        RefreshPublishedConnection(runtimeUrl);
        var hostHandshake = payload is null && !developmentRestartPending
            ? null
            : await _tryGetHostHandshakeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
        var developmentHostStopped = false;
        if (developmentRestartPending
            && hostHandshake is not null
            && string.Equals(hostHandshake.ProtocolIdentity, HostProtocol.Identity, StringComparison.Ordinal))
        {
            if (runtimeHostPath is null || !File.Exists(runtimeHostPath))
            {
                throw new InvalidOperationException(
                    "Unable to locate the development Sunder Host before replacing the running instance.");
            }
            AppSessionLog.WriteInfo("Restarting the managed Sunder Host for this development App session.");
            await _shutdownRuntimeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
            if (!await WaitForStoppedRuntimeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "The previous development Sunder Host did not stop in time for replacement.");
            }
            developmentHostStopped = true;
            hostHandshake = null;
        }
        var replacingPayload = payload is not null
                               && (payload.ReplacesCurrent
                                   || (hostHandshake is null
                                    ? payload.Previous is not null
                                    : !_userHostPayloadStore!.Matches(payload, hostHandshake)));
        UserHostPayload? rollbackPayload = null;
        var rollbackRuntimeUrl = runtimeUrl;
        var previousHostStoppedForReplacement = false;
        var payloadLaunchAttempted = false;
        var payloadLaunchAccepted = false;
        try
        {
            if (replacingPayload && hostHandshake is not null)
            {
                AppSessionLog.WriteInfo(
                    $"Replacing current-user Sunder Host {hostHandshake.Product.InformationalVersion} with {payload!.Version}.");
                rollbackPayload = payload.Previous;
                await _shutdownRuntimeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
                if (!await WaitForStoppedRuntimeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "The previous current-user Sunder Host did not stop in time for its payload update.");
                }
                previousHostStoppedForReplacement = rollbackPayload is not null;
            }
            else if (replacingPayload)
            {
                AppSessionLog.WriteInfo($"Repairing the current-user Sunder Host with payload {payload!.Version}.");
            }

            var stoppedPublishedHostUrl = await RejectOrRemoveMismatchedPublishedRuntimeAsync(
                    runtimeUrl,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stoppedPublishedHostUrl is not null && payload is not null)
            {
                rollbackPayload ??= payload.Previous ?? payload;
                rollbackRuntimeUrl = stoppedPublishedHostUrl;
                previousHostStoppedForReplacement = true;
            }
            RefreshPublishedConnection(runtimeUrl);
            var runningHandshake = await _tryGetRuntimeHandshakeAsync(runtimeUrl, cancellationToken);
            var migratingDirectRuntime = payload is not null
                                         && hostHandshake is null
                                         && CanReuseRunningRuntime(runningHandshake);
            if (migratingDirectRuntime)
            {
                AppSessionLog.WriteInfo("Replacing the legacy direct Runtime with the current-user Sunder Host.");
                if (replacingPayload)
                {
                    rollbackPayload = payload!.Previous;
                }
                await _shutdownRuntimeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
                if (!await WaitForStoppedRuntimeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "The legacy direct Sunder Runtime did not stop in time for Host activation.");
                }
                previousHostStoppedForReplacement = rollbackPayload is not null;
                runningHandshake = null;
            }
            else if (CanReuseRunningRuntime(runningHandshake))
            {
                if (payload is not null && hostHandshake is not null)
                {
                    _userHostPayloadStore!.Commit(payload);
                }
                CompleteDevelopmentRestart(developmentRestartPending);
                return;
            }

            if (runningHandshake is null
                && await _tryEnsureSupervisedRuntimeStartedAsync(runtimeUrl, cancellationToken).ConfigureAwait(false))
            {
                if (await WaitForAcceptableRuntimeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false))
                {
                    if (payload is not null)
                    {
                        await ValidateAndCommitPayloadAsync(payload, runtimeUrl, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await GetCompatibleHostHandshakeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
                    }
                    CompleteDevelopmentRestart(developmentRestartPending);
                    return;
                }
                throw new InvalidOperationException(
                    $"The Sunder Host at '{runtimeUrl}' did not make its Runtime worker ready within {_startupTimeout.TotalSeconds:0} seconds.");
            }

            var replaceExistingRuntime = payload is not null || developmentHostStopped;
            if (ShouldReplaceRunningRuntime(runningHandshake))
            {
                replaceExistingRuntime = true;
                var runningVersion = runningHandshake!.Product?.ProductVersion ?? "unknown";
                var reason = RuntimeProtocolCompatibility.GetIncompatibility(runningHandshake);
                AppSessionLog.WriteInfo($"Replacing managed Sunder Host Runtime worker {runningVersion}: {reason}");
                await _shutdownRuntimeAsync(runtimeUrl, cancellationToken);
                var stopped = await WaitForStoppedRuntimeAsync(runtimeUrl, cancellationToken);
                if (!stopped)
                {
                    throw new InvalidOperationException(
                        $"Sunder.Runtime.Host {runningVersion} did not shut down in time to start the bundled compatible Runtime.");
                }
            }
            else if (runningHandshake is not null)
            {
                throw CreateRuntimeUrlOccupiedException(runtimeUrl, runningHandshake.Product?.ProductName);
            }
            else if (await _isRuntimeHealthyAsync(runtimeUrl, cancellationToken))
            {
                throw CreateRuntimeUrlOccupiedException(runtimeUrl, serviceName: null);
            }

            if (runtimeHostPath is null)
            {
                throw new InvalidOperationException(
                    "Unable to locate an installed Sunder Host next to Sunder.App. Use --runtime-host-path or SUNDER_RUNTIME_HOST_PATH to point at the Host executable or folder."
                );
            }

            if (!runtimeUrl.IsLoopback)
            {
                throw new InvalidOperationException(
                    $"The managed Runtime cannot be launched at non-loopback URL '{runtimeUrl}'. Connect only with matching authenticated connection information, or launch the Runtime manually with the explicit development-only non-loopback override.");
            }
            var launchingSupervisor = payload is not null || IsSupervisorExecutable(runtimeHostPath);

            if (!_isRuntimeLeaseAvailable())
            {
                var stopped = await WaitForStoppedRuntimeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
                if (!stopped)
                {
                    throw new InvalidOperationException(
                        "The previous Sunder.Runtime.Host is still releasing local state. Retry after it finishes shutting down.");
                }
                replaceExistingRuntime = true;
            }

            var staleConnection = RuntimeConnectionInfoStore.Load(_connectionInfoPath);
            if (staleConnection is not null)
            {
                RuntimeConnectionInfoStore.DeleteIfMatches(staleConnection, _connectionInfoPath);
            }
            _runtimeConnectionState.RuntimeUrl = runtimeUrl;
            var launchStopwatch = Stopwatch.StartNew();
            if (replacingPayload)
            {
                rollbackPayload = payload!.Previous;
            }
            payloadLaunchAttempted = payload is not null;
            await StartRuntimeHostProcessAsync(RuntimeHostStartInfoFactory.Create(
                runtimeHostPath,
                runtimeUrl,
                _connectionInfoPath,
                managedSupervisor: launchingSupervisor), replaceExistingRuntime, cancellationToken).ConfigureAwait(false);
            payloadLaunchAccepted = payload is not null;
            CompleteDevelopmentRestart(developmentRestartPending);
            AppSessionLog.WriteInfo(
                $"Runtime launcher accepted '{runtimeUrl}' in {launchStopwatch.ElapsedMilliseconds} ms.");

            var readinessStopwatch = Stopwatch.StartNew();
            var started = await WaitForAcceptableRuntimeAsync(
                runtimeUrl,
                cancellationToken,
                ensureSupervisedRuntime: launchingSupervisor);
            if (!started)
            {
                _runtimeConnectionState.RuntimeUrl = runtimeUrl;
                throw new InvalidOperationException(
                    $"Sunder Host did not publish a compatible authenticated Runtime connection at '{runtimeUrl}' within {_startupTimeout.TotalSeconds:0} seconds."
                    + (payload is null ? " The managed Runtime may still be starting; Retry will reuse it." : string.Empty));
            }
            AppSessionLog.WriteInfo(
                $"Runtime authenticated connection became available at '{runtimeUrl}' in {readinessStopwatch.ElapsedMilliseconds} ms.");
            if (payload is not null)
            {
                await ValidateAndCommitPayloadAsync(payload, runtimeUrl, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (launchingSupervisor)
            {
                await GetCompatibleHostHandshakeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
            }
            CompleteDevelopmentRestart(developmentRestartPending);
        }
        catch
        {
            var canAbandonPayload = !payloadLaunchAttempted;
            if (rollbackPayload is not null)
            {
                if (payloadLaunchAccepted
                    && await TryStopFailedPayloadAsync(runtimeUrl).ConfigureAwait(false))
                {
                    canAbandonPayload = true;
                    await TryRestorePreviousPayloadAsync(rollbackPayload, rollbackRuntimeUrl).ConfigureAwait(false);
                }
                else if (!payloadLaunchAttempted && previousHostStoppedForReplacement)
                {
                    await TryRestorePreviousPayloadAsync(rollbackPayload, rollbackRuntimeUrl).ConfigureAwait(false);
                }
            }
            else if (payloadLaunchAccepted)
            {
                canAbandonPayload = await TryStopFailedPayloadAsync(runtimeUrl).ConfigureAwait(false);
            }
            if (canAbandonPayload && payload is not null)
            {
                try
                {
                    _userHostPayloadStore!.Abandon(payload);
                }
                catch (Exception exception)
                {
                    AppSessionLog.WriteError("Could not remove an abandoned current-user Host payload.", exception);
                }
            }
            throw;
        }
    }

    private async Task TryRestorePreviousPayloadAsync(UserHostPayload previous, Uri runtimeUrl)
    {
        try
        {
            using var deadline = new CancellationTokenSource(_startupTimeout);
            var published = RuntimeConnectionInfoStore.Load(_connectionInfoPath);
            if (published is not null)
            {
                RuntimeConnectionInfoStore.DeleteIfMatches(published, _connectionInfoPath);
            }
            await StartRuntimeHostProcessAsync(
                    RuntimeHostStartInfoFactory.Create(
                        previous.ExecutablePath,
                        runtimeUrl,
                        _connectionInfoPath,
                        managedSupervisor: true),
                    replaceExisting: true,
                    deadline.Token)
                .ConfigureAwait(false);
            if (await WaitForAcceptableRuntimeAsync(
                    runtimeUrl,
                    deadline.Token,
                    ensureSupervisedRuntime: true).ConfigureAwait(false))
            {
                await ValidateAndCommitPayloadAsync(previous, runtimeUrl, deadline.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError("Failed to restore the previous current-user Sunder Host payload.", exception);
        }
    }

    private async Task<bool> TryStopFailedPayloadAsync(Uri runtimeUrl)
    {
        try
        {
            using var deadline = new CancellationTokenSource(_startupTimeout);
            if (await _tryGetHostHandshakeAsync(runtimeUrl, deadline.Token).ConfigureAwait(false) is null)
            {
                AppSessionLog.WriteInfo(
                    "Preserving an uncommitted Host payload because persistent launch was accepted but no authenticated Host was observed.");
                return false;
            }
            await _shutdownRuntimeAsync(runtimeUrl, deadline.Token).ConfigureAwait(false);
            if (await WaitForStoppedRuntimeAsync(runtimeUrl, deadline.Token).ConfigureAwait(false))
            {
                return true;
            }
            AppSessionLog.WriteError(
                "The failed current-user Host payload did not stop before cleanup.",
                new InvalidOperationException("Host shutdown did not release the listener and lifecycle lock."));
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError("Failed to stop an uncommitted current-user Host payload.", exception);
        }
        return false;
    }

    private async Task ValidateAndCommitPayloadAsync(
        UserHostPayload payload,
        Uri runtimeUrl,
        CancellationToken cancellationToken)
    {
        var handshake = await GetCompatibleHostHandshakeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
        if (!_userHostPayloadStore!.Matches(payload, handshake))
        {
            throw new InvalidOperationException(
                $"Sunder Host reported version '{handshake.Product.InformationalVersion}' instead of staged payload version '{payload.Version}'.");
        }
        _userHostPayloadStore.Commit(payload);
    }

    private async Task<HostHandshakeResponse> GetCompatibleHostHandshakeAsync(
        Uri runtimeUrl,
        CancellationToken cancellationToken)
    {
        var handshake = await _tryGetHostHandshakeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
        var incompatibility = HostProtocolCompatibility.GetManagedSupervisorIncompatibility(handshake);
        if (incompatibility is not null)
        {
            throw new InvalidOperationException(incompatibility);
        }
        return handshake!;
    }

    private async Task<Uri?> RejectOrRemoveMismatchedPublishedRuntimeAsync(
        Uri runtimeUrl,
        CancellationToken cancellationToken)
    {
        var published = RuntimeConnectionInfoStore.Load(_connectionInfoPath);
        if (published is null || published.Matches(runtimeUrl))
        {
            return null;
        }

        _runtimeConnectionState.SetConnection(published);
        try
        {
            var hostHandshake = await _tryGetHostHandshakeAsync(published.RuntimeUrl, cancellationToken)
                .ConfigureAwait(false);
            if (hostHandshake is not null
                && string.Equals(
                    hostHandshake.ProtocolIdentity,
                    HostProtocol.Identity,
                    StringComparison.Ordinal))
            {
                AppSessionLog.WriteInfo(
                    $"Stopping the managed Sunder Host at '{published.RuntimeUrl}' before rebinding to '{runtimeUrl}'.");
                await _shutdownRuntimeAsync(published.RuntimeUrl, cancellationToken).ConfigureAwait(false);
                if (!await WaitForStoppedRuntimeAsync(published.RuntimeUrl, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"The managed Sunder Host at '{published.RuntimeUrl}' did not stop before rebinding to '{runtimeUrl}'.");
                }
                RuntimeConnectionInfoStore.DeleteIfMatches(published, _connectionInfoPath);
                return published.RuntimeUrl;
            }

            var handshake = await _tryGetRuntimeHandshakeAsync(published.RuntimeUrl, cancellationToken)
                .ConfigureAwait(false);
            if (handshake is not null
                || await _isRuntimeHealthyAsync(published.RuntimeUrl, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"A Runtime published at '{published.RuntimeUrl}' is still running. Stop it before starting the managed Runtime at '{runtimeUrl}'.");
            }

            if (!_isRuntimeLeaseAvailable())
            {
                var stopped = await WaitForStoppedRuntimeAsync(published.RuntimeUrl, cancellationToken)
                    .ConfigureAwait(false);
                if (!stopped)
                {
                    throw new InvalidOperationException(
                        $"The Runtime previously published at '{published.RuntimeUrl}' is still releasing local state. Retry after it finishes shutting down.");
                }
            }

            RuntimeConnectionInfoStore.DeleteIfMatches(published, _connectionInfoPath);
            return null;
        }
        finally
        {
            _runtimeConnectionState.RuntimeUrl = runtimeUrl;
        }
    }

    private async Task StartRuntimeHostProcessAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        try
        {
            await _launchRuntimeAsync(startInfo, replaceExisting, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to start Sunder.Runtime.Host using '{startInfo.FileName}'.",
                ex);
        }
    }

    private async Task<bool> WaitForAcceptableRuntimeAsync(
        Uri runtimeUrl,
        CancellationToken cancellationToken,
        bool ensureSupervisedRuntime = false)
    {
        var startedAt = _timeProvider.GetTimestamp();
        while (true)
        {
            RefreshPublishedConnection(runtimeUrl);
            var handshake = await _tryGetRuntimeHandshakeAsync(runtimeUrl, cancellationToken);
            if (CanReuseRunningRuntime(handshake))
            {
                return true;
            }

            var remaining = _startupTimeout - _timeProvider.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            if (ensureSupervisedRuntime)
            {
                using var startDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startDeadline.CancelAfter(remaining);
                try
                {
                    if (await _tryEnsureSupervisedRuntimeStartedAsync(runtimeUrl, startDeadline.Token)
                        .ConfigureAwait(false))
                    {
                        ensureSupervisedRuntime = false;
                        continue;
                    }
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested && startDeadline.IsCancellationRequested)
                {
                    return false;
                }
            }

            await _delayAsync(
                remaining < StartupPollInterval ? remaining : StartupPollInterval,
                cancellationToken);
        }
    }

    private async Task<bool> WaitForStoppedRuntimeAsync(Uri runtimeUrl, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (!await _isRuntimeHealthyAsync(runtimeUrl, cancellationToken)
                && _isRuntimeLeaseAvailable()
                && _isHostInstanceLockAvailable())
            {
                return true;
            }

            if (attempt < 119)
            {
                await _delayAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }

        return false;
    }

    private string? ResolveRuntimeHostPath()
    {
        return ResolveFromPath(_startupOptions.RuntimeHostPath)
            ?? ResolveFromPath(Path.Combine(AppContext.BaseDirectory, "RuntimeHost"))
            ?? ResolveFromPath(AppContext.BaseDirectory)
#if DEBUG
            ?? ResolveFromPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "Sunder.Host.Supervisor",
                    "bin",
                    "Debug",
                    "net10.0"
                )
            )
#endif
            ;
    }

    internal static bool CanReuseRunningRuntime(
        RuntimeHandshakeResponse? handshake)
        => RuntimeProtocolCompatibility.IsCompatible(handshake);

    internal static bool ShouldReplaceRunningRuntime(
        RuntimeHandshakeResponse? handshake)
        => handshake is not null
           && string.Equals(handshake.ProtocolIdentity, RuntimeProtocol.Identity, StringComparison.Ordinal)
           && !RuntimeProtocolCompatibility.IsCompatible(handshake);

    private void CompleteDevelopmentRestart(bool wasPending)
    {
        if (wasPending)
        {
            _managedHostRestartCompleted = true;
        }
    }

    private static bool ShouldRestartManagedHostForDefaultDevelopmentLaunch(
        Func<string?>? resolveRuntimeHostPath,
        Func<Uri, CancellationToken, Task<RuntimeHandshakeResponse?>>? tryGetRuntimeHandshakeAsync,
        Func<Uri, CancellationToken, Task<bool>>? isRuntimeHealthyAsync,
        Func<Uri, CancellationToken, Task>? shutdownRuntimeAsync,
        Action<ProcessStartInfo>? startProcess,
        UserHostPayloadStore? userHostPayloadStore)
    {
#if DEBUG
        return resolveRuntimeHostPath is null
               && tryGetRuntimeHandshakeAsync is null
               && isRuntimeHealthyAsync is null
               && shutdownRuntimeAsync is null
               && startProcess is null
               && userHostPayloadStore is null;
#else
        return false;
#endif
    }

    private void RefreshPublishedConnection(Uri runtimeUrl)
    {
        var published = RuntimeConnectionInfoStore.Load(_connectionInfoPath);
        if (published is null
            || !published.Matches(runtimeUrl))
        {
            return;
        }

        _runtimeConnectionState.SetConnection(published);
    }

    private static InvalidOperationException CreateRuntimeUrlOccupiedException(Uri runtimeUrl, string? serviceName)
    {
        var serviceDescription = string.IsNullOrWhiteSpace(serviceName)
            ? "a service that does not identify as Sunder.Runtime.Host"
            : $"'{serviceName}'";

        return new InvalidOperationException(
            $"Runtime URL '{runtimeUrl}' is already occupied by {serviceDescription}. Stop that service or choose a different runtime URL.");
    }

    private static string? ResolveFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        if (!Directory.Exists(fullPath))
        {
            return null;
        }

        foreach (var candidate in GetRuntimeFileCandidates(fullPath))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetRuntimeFileCandidates(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(directory, "Sunder.Host.Supervisor.exe");
        }
        else
        {
            yield return Path.Combine(directory, "Sunder.Host.Supervisor");
        }

        yield return Path.Combine(directory, "Sunder.Host.Supervisor.dll");
    }

    private static bool IsSupervisorExecutable(string path)
    {
        var fileName = Path.GetFileName(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(fileName, RuntimeHostName, comparison)
               || string.Equals(fileName, $"{RuntimeHostName}.exe", comparison)
               || string.Equals(fileName, $"{RuntimeHostName}.dll", comparison);
    }
}
