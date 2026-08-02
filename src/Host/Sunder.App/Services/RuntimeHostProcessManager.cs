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
    private readonly IHostServiceManager _hostServiceManager;
    private readonly IDisposable? _ownedHostServiceManager;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly string _connectionInfoPath;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _startupTimeout;
    private readonly Func<bool> _isRuntimeLeaseAvailable;
    private readonly Func<Uri, CancellationToken, Task<HostHandshakeResponse?>> _tryGetHostHandshakeAsync;
    private readonly Func<Uri, CancellationToken, Task<bool>> _tryEnsureSupervisedRuntimeStartedAsync;
    private readonly Func<bool> _isHostInstanceLockAvailable;
    private readonly UserHostPayloadStore? _userHostPayloadStore;
    private readonly SemaphoreSlim _startupSemaphore = new(1, 1);
    private readonly RuntimeHealthProbe? _healthProbe;

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
        IHostServiceManager? hostServiceManager = null)
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
        if (hostServiceManager is not null)
        {
            _hostServiceManager = hostServiceManager;
        }
        else if (startProcess is not null)
        {
            _hostServiceManager = new LegacyInjectedHostServiceManager(startProcess);
        }
        else
        {
            _hostServiceManager = RuntimePersistentLauncher.Create();
            _ownedHostServiceManager = _hostServiceManager as IDisposable;
        }
        _delayAsync = delayAsync ?? Task.Delay;
        _connectionInfoPath = connectionInfoPath ?? HostConnectionInfoStore.GetDefaultPath();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startupTimeout = startupTimeout ?? DefaultStartupTimeout;
        _isRuntimeLeaseAvailable = isRuntimeLeaseAvailable ?? (() => RuntimeLocalState.IsLeaseAvailable());
        _userHostPayloadStore = userHostPayloadStore
            ?? (ShouldUseDefaultHostPayloadStore(
                    startupOptions,
                    resolveRuntimeHostPath,
                    tryGetRuntimeHandshakeAsync,
                    isRuntimeHealthyAsync,
                    shutdownRuntimeAsync,
                    startProcess,
                    hostServiceManager)
                ? new UserHostPayloadStore()
                : null);
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
        _ownedHostServiceManager?.Dispose();
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
        using var payload = runtimeUrl.IsLoopback ? _userHostPayloadStore?.Prepare() : null;
        var runtimeHostPath = payload?.ExecutablePath ?? _resolveRuntimeHostPath();
        RefreshPublishedConnection(runtimeUrl);
        var hostHandshake = payload is null
            ? null
            : await _tryGetHostHandshakeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
        var runningPayloadMatches = payload is not null
                                    && !payload.ReplacesCurrent
                                    && hostHandshake is not null
                                    && _userHostPayloadStore!.Matches(payload, hostHandshake);
        var replacingPayload = payload is not null
                               && !runningPayloadMatches
                               && (payload.ReplacesCurrent
                                   || payload.Previous is not null
                                   || hostHandshake is not null);
        UserHostPayload? rollbackPayload = null;
        var rollbackRuntimeUrl = runtimeUrl;
        var previousHostStoppedForReplacement = false;
        var payloadLaunchAttempted = false;
        var payloadLaunchAccepted = false;
        HostServiceLaunchReceipt? acceptedLaunchReceipt = null;
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
                    return;
                }
                throw new InvalidOperationException(
                    $"The Sunder Host at '{runtimeUrl}' did not make its Runtime worker ready within {_startupTimeout.TotalSeconds:0} seconds.");
            }

            var replaceExistingRuntime = payload is not null;
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
                RefreshPublishedConnection(runtimeUrl);
                var lateHostHandshake = await _tryGetHostHandshakeAsync(runtimeUrl, cancellationToken)
                    .ConfigureAwait(false);
                if (lateHostHandshake is not null)
                {
                    if (payload is not null
                        && (payload.ReplacesCurrent
                            || !_userHostPayloadStore!.Matches(payload, lateHostHandshake)))
                    {
                        throw new InvalidOperationException(
                            $"A different managed Sunder Host became ready at '{runtimeUrl}' while its replacement was being prepared. Retry to reconcile that Host safely.");
                    }
                    if (await _tryEnsureSupervisedRuntimeStartedAsync(runtimeUrl, cancellationToken)
                            .ConfigureAwait(false)
                        && await WaitForAcceptableRuntimeAsync(runtimeUrl, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        if (payload is not null)
                        {
                            await ValidateAndCommitPayloadAsync(payload, runtimeUrl, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await GetCompatibleHostHandshakeAsync(runtimeUrl, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        return;
                    }
                }
                var lateRuntimeHandshake = await _tryGetRuntimeHandshakeAsync(runtimeUrl, cancellationToken)
                    .ConfigureAwait(false);
                if (CanReuseRunningRuntime(lateRuntimeHandshake))
                {
                    if (payload is null)
                    {
                        return;
                    }
                    if (lateHostHandshake is not null
                        && _userHostPayloadStore!.Matches(payload, lateHostHandshake)
                        && !payload.ReplacesCurrent)
                    {
                        _userHostPayloadStore.Commit(payload);
                        return;
                    }
                    throw new InvalidOperationException(
                        $"A different managed Sunder Host became ready at '{runtimeUrl}' while its replacement was being prepared. Retry to reconcile that Host safely.");
                }
                if (lateHostHandshake is not null)
                {
                    throw new InvalidOperationException(
                        $"The managed Sunder Host became available at '{runtimeUrl}', but its Runtime worker was not ready. Retry will reuse that Host.");
                }
                RuntimeConnectionInfoStore.DeleteIfMatches(staleConnection, _connectionInfoPath);
            }
            _runtimeConnectionState.RuntimeUrl = runtimeUrl;
            var launchStopwatch = Stopwatch.StartNew();
            if (replacingPayload)
            {
                rollbackPayload = payload!.Previous;
            }
            payloadLaunchAttempted = payload is not null;
            var launchReceipt = await StartRuntimeHostProcessAsync(RuntimeHostStartInfoFactory.Create(
                runtimeHostPath,
                runtimeUrl,
                _connectionInfoPath,
                managedSupervisor: launchingSupervisor,
                deploymentIdentity: payload?.DeploymentIdentity), replaceExistingRuntime, cancellationToken).ConfigureAwait(false);
            acceptedLaunchReceipt = launchReceipt;
            payloadLaunchAccepted = payload is not null;
            AppSessionLog.WriteInfo(
                $"Sunder Host launch accepted ({HostServiceStartupException.DescribeReceipt(launchReceipt)}) for '{runtimeUrl}' in {launchStopwatch.ElapsedMilliseconds} ms.");

            var readinessStopwatch = Stopwatch.StartNew();
            var started = await WaitForAcceptableRuntimeAsync(
                runtimeUrl,
                cancellationToken,
                ensureSupervisedRuntime: launchingSupervisor,
                launchReceipt: launchReceipt);
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
        }
        catch (Exception startupException)
        {
            using var compensationDeadline = new CancellationTokenSource(_startupTimeout);
            var canAbandonPayload = !payloadLaunchAttempted;
            var launchedServiceTerminationWasObserved = startupException is HostServiceStartupException
            {
                Failure: HostServiceStartupFailure.TerminalState,
                Observation.State: HostServiceState.Stopped or HostServiceState.Failed,
            };
            var previousServiceWasDisplaced = startupException is IHostServiceReplacementFailure;
            if (rollbackPayload is not null)
            {
                if (payloadLaunchAccepted
                    && (launchedServiceTerminationWasObserved
                        || await TryStopFailedPayloadAsync(
                                 runtimeUrl,
                                 payload!.DeploymentIdentity,
                                 acceptedLaunchReceipt,
                                 compensationDeadline.Token)
                             .ConfigureAwait(false)))
                {
                    canAbandonPayload = true;
                    await TryRestorePreviousPayloadAsync(
                            rollbackPayload,
                            rollbackRuntimeUrl,
                            compensationDeadline.Token)
                        .ConfigureAwait(false);
                }
                else if (!payloadLaunchAccepted
                         && (previousHostStoppedForReplacement || previousServiceWasDisplaced))
                {
                    await TryRestorePreviousPayloadAsync(
                            rollbackPayload,
                            rollbackRuntimeUrl,
                            compensationDeadline.Token)
                        .ConfigureAwait(false);
                }
            }
            else if (payloadLaunchAccepted)
            {
                canAbandonPayload = launchedServiceTerminationWasObserved
                                    || await TryStopFailedPayloadAsync(
                                             runtimeUrl,
                                             payload!.DeploymentIdentity,
                                             acceptedLaunchReceipt,
                                             compensationDeadline.Token)
                                         .ConfigureAwait(false);
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

    private async Task TryRestorePreviousPayloadAsync(
        UserHostPayload previous,
        Uri runtimeUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var published = RuntimeConnectionInfoStore.Load(_connectionInfoPath);
            if (published is not null)
            {
                RuntimeConnectionInfoStore.DeleteIfMatches(published, _connectionInfoPath);
            }
            var launchReceipt = await StartRuntimeHostProcessAsync(
                    RuntimeHostStartInfoFactory.Create(
                        previous.ExecutablePath,
                        runtimeUrl,
                     _connectionInfoPath,
                     managedSupervisor: true,
                     deploymentIdentity: previous.DeploymentIdentity),
                 replaceExisting: true,
                 cancellationToken)
                .ConfigureAwait(false);
            if (await WaitForAcceptableRuntimeAsync(
                    runtimeUrl,
                    cancellationToken,
                    ensureSupervisedRuntime: true,
                    launchReceipt: launchReceipt).ConfigureAwait(false))
            {
                await ValidateAndCommitPayloadAsync(previous, runtimeUrl, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError("Failed to restore the previous current-user Sunder Host payload.", exception);
        }
    }

    private async Task<bool> TryStopFailedPayloadAsync(
        Uri runtimeUrl,
        string attemptedDeploymentIdentity,
        HostServiceLaunchReceipt? launchReceipt,
        CancellationToken cancellationToken)
    {
        try
        {
            var handshake = await _tryGetHostHandshakeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
            if (handshake is null)
            {
                if (launchReceipt is not null
                    && await _hostServiceManager.TryStopAsync(launchReceipt, cancellationToken)
                        .ConfigureAwait(false)
                    && await WaitForStoppedRuntimeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
                AppSessionLog.WriteInfo(
                    "Preserving an uncommitted Host payload because persistent launch was accepted but no authenticated Host was observed.");
                return false;
            }
            if (!string.Equals(
                    handshake.DeploymentIdentity,
                    attemptedDeploymentIdentity,
                    StringComparison.Ordinal))
            {
                if (launchReceipt is not null
                    && await _hostServiceManager.TryStopAsync(launchReceipt, cancellationToken)
                        .ConfigureAwait(false)
                    && await WaitForStoppedRuntimeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
                AppSessionLog.WriteInfo(
                    "Preserving an uncommitted Host payload because a different authenticated Host owns the Runtime URL.");
                return false;
            }
            await _shutdownRuntimeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
            if (await WaitForStoppedRuntimeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false))
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
                $"Sunder Host reported deployment identity '{handshake.DeploymentIdentity ?? "missing"}' instead of staged payload identity '{payload.DeploymentIdentity}'.");
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

    private async Task<HostServiceLaunchReceipt> StartRuntimeHostProcessAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _hostServiceManager
                .ReconcileAndLaunchAsync(startInfo, replaceExisting, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HostServiceReplacementException)
        {
            throw;
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
        bool ensureSupervisedRuntime = false,
        HostServiceLaunchReceipt? launchReceipt = null)
    {
        var startedAt = _timeProvider.GetTimestamp();
        var loggedProcessId = launchReceipt?.ProcessId;
        while (true)
        {
            RefreshPublishedConnection(runtimeUrl);
            var handshake = await _tryGetRuntimeHandshakeAsync(runtimeUrl, cancellationToken);
            if (CanReuseRunningRuntime(handshake))
            {
                return true;
            }

            if (launchReceipt is not null)
            {
                HostServiceObservation observation;
                try
                {
                    observation = await _hostServiceManager
                        .ObserveAsync(launchReceipt, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw HostServiceStartupException.FromObservationError(launchReceipt, exception);
                }

                if (observation.ProcessId is { } observedProcessId
                    && observedProcessId != loggedProcessId)
                {
                    loggedProcessId = observedProcessId;
                    AppSessionLog.WriteInfo(
                        $"Sunder Host service process observed ({HostServiceStartupException.DescribeReceipt(launchReceipt, observedProcessId)}).");
                }
                if (observation.IsTerminal)
                {
                    throw HostServiceStartupException.FromTerminalObservation(launchReceipt, observation);
                }
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

    private static bool ShouldUseDefaultHostPayloadStore(
        AppStartupOptions startupOptions,
        Func<string?>? resolveRuntimeHostPath,
        Func<Uri, CancellationToken, Task<RuntimeHandshakeResponse?>>? tryGetRuntimeHandshakeAsync,
        Func<Uri, CancellationToken, Task<bool>>? isRuntimeHealthyAsync,
        Func<Uri, CancellationToken, Task>? shutdownRuntimeAsync,
        Action<ProcessStartInfo>? startProcess,
        IHostServiceManager? hostServiceManager)
    {
        return string.IsNullOrWhiteSpace(startupOptions.RuntimeHostPath)
               && resolveRuntimeHostPath is null
               && tryGetRuntimeHandshakeAsync is null
               && isRuntimeHealthyAsync is null
               && shutdownRuntimeAsync is null
               && startProcess is null
               && hostServiceManager is null;
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

    private sealed class LegacyInjectedHostServiceManager(Action<ProcessStartInfo> startProcess)
        : IHostServiceManager
    {
        public Task<HostServiceLaunchReceipt> ReconcileAndLaunchAsync(
            ProcessStartInfo startInfo,
            bool replaceExisting,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            startProcess(startInfo);
            return Task.FromResult(new HostServiceLaunchReceipt("legacy-injected", "sunder-host"));
        }

        public Task<HostServiceObservation> ObserveAsync(
            HostServiceLaunchReceipt receipt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(HostServiceObservation.Unknown(receipt.ProcessId));
        }
    }
}
