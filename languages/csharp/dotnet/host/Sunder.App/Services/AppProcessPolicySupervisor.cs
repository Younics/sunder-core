using System.Diagnostics;
using System.Reflection;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Compatibility;

namespace Sunder.App.Services;

internal interface IAppPackagePolicy
{
    Task RequireLoadAsync(PackageUiSnapshotDescriptor snapshot, CancellationToken token);
    bool OwnsRetirement(RuntimeAppActivationRetirementRequirement requirement) => false;
    bool HasRetiredActivation(RuntimeAppActivationRetirementRequirement requirement) => false;
    void SetRetirementHandler(Func<IReadOnlyList<RuntimeAppActivationRetirementRequirement>, CancellationToken, Task> handler) { }

    async Task<AppPackageLoadAdmission> AcquireLoadAsync(PackageUiSnapshotDescriptor snapshot, Guid activationId, CancellationToken token)
    {
        await RequireLoadAsync(snapshot, token).ConfigureAwait(false);
        return new(activationId, permitId: null);
    }
}

internal sealed class UnavailableAppPackagePolicy : IAppPackagePolicy
{
    internal static UnavailableAppPackagePolicy Instance { get; } = new();
    public Task RequireLoadAsync(PackageUiSnapshotDescriptor snapshot, CancellationToken token)
        => Task.FromException(new InvalidOperationException(PackagePolicyErrorCodes.AppProcessSupervisionRequired));
}

internal interface IAppPolicyConnection
{
    string? DiagnosticPhase => null;
    Task<RuntimeHandshakeResponse> HandshakeAsync(CancellationToken token);
    Task<RuntimeAppPolicySessionDescriptor> OpenAsync(RuntimeAppPolicySessionOpenRequest request, CancellationToken token);
    RuntimeHandshakeResponse HeartbeatHandshake(CancellationToken token);
    RuntimeAppPolicySessionStatus Heartbeat(RuntimeAppPolicySessionRequest request, CancellationToken token);
    Task<RuntimeAppPackageLoadPermitResult> PermitAsync(RuntimeAppPackageLoadPermitRequest request, CancellationToken token);
    Task<RuntimeAppActivationRetirementResult> RetireAsync(RuntimeAppActivationRetirementRequest request, CancellationToken token)
        => Task.FromException<RuntimeAppActivationRetirementResult>(new NotSupportedException("App activation retirement is unavailable."));
    Task<RuntimeAppPolicySessionStatus> ShutdownAsync(RuntimeAppPolicySessionRequest request, CancellationToken token);
}

internal sealed partial class AppProcessPolicySupervisor : IAppPackagePolicy, IAsyncDisposable
{
    private readonly IAppPolicyConnection _connection;
    private readonly Func<Guid, RuntimeAppProcessIdentity> _identity;
    private readonly Action _shutdownApp;
    private readonly Action<string> _failFast;
    private readonly Func<RuntimeContinuousClockReading> _clock;
    private readonly TimeProvider _time;
    private readonly Action<string> _writeDiagnostic;
    private readonly SemaphoreSlim _openGate = new(1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _proofGate = new();
    private RuntimeAppPolicySessionRequest? _session;
    private RuntimeContinuousClockReading? _lastProof;
    private Task _heartbeats = Task.CompletedTask;
    private Thread? _watchdog;
    private bool _armed;
    private int _failed;
    private int _shutdownRequested;
    private readonly object _retirementGate = new();
    private Func<IReadOnlyList<RuntimeAppActivationRetirementRequirement>, CancellationToken, Task>? _retirementHandler;
    private Task _retirements = Task.CompletedTask;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _retiredActivations = new();

    public bool OwnsRetirement(RuntimeAppActivationRetirementRequirement requirement)
        => _session is { } session && requirement.PolicySessionId == session.SessionId;

    public bool HasRetiredActivation(RuntimeAppActivationRetirementRequirement requirement)
        => OwnsRetirement(requirement) && _retiredActivations.TryGetValue(requirement.AppActivationId, out var packageId)
            && packageId == requirement.Package.PackageId;

    public void SetRetirementHandler(Func<IReadOnlyList<RuntimeAppActivationRetirementRequirement>, CancellationToken, Task> handler)
    {
        lock (_retirementGate) _retirementHandler = handler;
    }

    internal AppProcessPolicySupervisor(AppRuntimeProcessTransport transport, Action shutdownApp)
        : this(new Connection(new RuntimeManagementClient(transport.PolicyTransport), transport), runtimeId =>
        {
            using var process = Process.GetCurrentProcess();
            return new(process.Id, RuntimeProcessIdentity.Birth(process), RuntimeProcessIdentity.ExecutableHash(process),
                RuntimeProcessIdentity.BuildIdentity(typeof(AppProcessPolicySupervisor).Assembly), Guid.NewGuid(), runtimeId);
        }, shutdownApp, Environment.FailFast, RuntimeContinuousClock.Read, TimeProvider.System) { }

    internal AppProcessPolicySupervisor(IAppPolicyConnection connection, Func<Guid, RuntimeAppProcessIdentity> identity,
        Action shutdownApp, Action<string> failFast, Func<RuntimeContinuousClockReading> clock, TimeProvider time,
        Action<string>? writeDiagnostic = null)
    {
        _connection = connection; _identity = identity; _shutdownApp = shutdownApp; _failFast = failFast; _clock = clock; _time = time;
        _writeDiagnostic = writeDiagnostic ?? AppSessionLog.WritePolicySupervisionDiagnostic;
    }

    public async Task RequireLoadAsync(PackageUiSnapshotDescriptor snapshot, CancellationToken token)
        => _ = await GetPermitAsync(snapshot, null, token).ConfigureAwait(false);

    public async Task<AppPackageLoadAdmission> AcquireLoadAsync(PackageUiSnapshotDescriptor snapshot, Guid activationId, CancellationToken token)
    {
        if (activationId == Guid.Empty) throw new ArgumentException("An actual activation identity is required.", nameof(activationId));
        // Installed UI needs the same exact retirement authority as development
        // UI. Retiring an activation never removes its durable security subjects.
        var permit = await GetPermitAsync(snapshot, activationId, token).ConfigureAwait(false);
        return new(activationId, permit.PermitId, async cancellation =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(AppShutdownBudgets.GenerationRetirement);
            var retired = await _connection.RetireAsync(new(permit.Session, activationId, permit.PermitId!), timeout.Token).ConfigureAwait(false);
            if (retired.Session != permit.Session || retired.AppActivationId != activationId
                || retired.LoadPermitId != permit.PermitId || retired.State != "retired")
                throw new InvalidDataException("The Host did not acknowledge the exact App activation retirement.");
            _retiredActivations.TryAdd(activationId, snapshot.PackageId);
        }) { PolicySessionId = permit.Session.SessionId };
    }

    private async Task<RuntimeAppPackageLoadPermitResult> GetPermitAsync(PackageUiSnapshotDescriptor snapshot, Guid? activationId, CancellationToken token)
    {
        if (snapshot.PolicySubject is null || snapshot.SnapshotId is not { Length: 32 }) throw new InvalidOperationException(PackagePolicyErrorCodes.AppProcessSupervisionRequired);
        var implementedCapabilities = GetImplementedObservationCapabilities(snapshot.Target);
        await _openGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            SetLoadPhase("checking", snapshot.SessionGeneration);
            if (_failed != 0 || _shutdownRequested != 0 || _stopping.IsCancellationRequested) throw new InvalidOperationException(PackagePolicyErrorCodes.AppProcessTerminationRequired);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            if (_session is null)
            {
                SetLoadPhase("handshake", snapshot.SessionGeneration);
                var handshake = await _connection.HandshakeAsync(timeout.Token).ConfigureAwait(false);
                if (handshake.RuntimeInstanceId == Guid.Empty) throw new InvalidDataException("App supervision Runtime identity is missing.");
                var identity = _identity(handshake.RuntimeInstanceId);
                SetLoadPhase("opening", snapshot.SessionGeneration);
                var opened = await _connection.OpenAsync(new(identity), timeout.Token).ConfigureAwait(false);
                if (opened.Process != identity || opened.State != RuntimeAppPolicySessionStates.Active
                    || !Guid.TryParseExact(opened.SessionId, "N", out _)) throw new InvalidDataException("App supervision identity differs.");
                _session = new(opened.SessionId, identity.AppGenerationId, identity.RuntimeInstanceId);
                StartSupervision();
            }
            SetLoadPhase("permit", snapshot.SessionGeneration);
            var permit = await _connection.PermitAsync(new(_session, snapshot.PolicySubject)
            {
                SnapshotId = snapshot.SnapshotId, AppActivationId = activationId,
                AppTarget = activationId is null ? null : snapshot.Target,
                AppImplementedCapabilities = implementedCapabilities,
            }, timeout.Token).ConfigureAwait(false);
            if (!permit.Allowed || permit.Session != _session || permit.Package != snapshot.PolicySubject || string.IsNullOrWhiteSpace(permit.PermitId)
                || permit.AppActivationId != activationId
                || snapshot.SourceKind == PackageSourceKind.Installed && (permit.Development || permit.Lease?.State != RuntimePolicyLeaseStates.Valid)
                || snapshot.SourceKind == PackageSourceKind.Dev && !permit.Development)
                throw new InvalidOperationException(permit.ErrorCode ?? PackagePolicyErrorCodes.AppProcessSupervisionRequired);
            if (permit.Lease is not null) ValidateLease(permit.Lease);
            lock (_proofGate)
            {
                if (_failed != 0 || _shutdownRequested != 0) throw new InvalidOperationException(PackagePolicyErrorCodes.AppProcessTerminationRequired);
                UpdateProof();
                _armed = true;
            }
            return permit;
        }
        finally
        {
            SetLoadPhase("idle", snapshot.SessionGeneration);
            _openGate.Release();
        }
    }

    internal static IReadOnlyList<string>? GetImplementedObservationCapabilities(PackageTargetDescriptor target)
    {
        // This allow-list describes this App implementation, not just types found
        // in its SDK or a declaration supplied by the package. Older targets send
        // no new JSON member to older strict Hosts.
        var required = target.RequiredHostCapabilities.Where(capability => capability is
            SunderSdkCapabilities.WorkerExecutionObservationsV1 or SunderSdkCapabilities.PackageDevelopmentRevisionsV1)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (required.Length == 0) return null;
        if (target.Role != "app" || target.Kind != "avalonia")
            throw new InvalidDataException("Observation capabilities require a managed App target.");
        return required;
    }

    private void StartSupervision()
    {
        SetHeartbeatPhase("scheduled");
        WriteDiagnostic("started");
        // Even synchronously completed open/permit calls can originate on the UI
        // dispatcher or inside a package RPC. Neither context owns supervision.
        if (ExecutionContext.IsFlowSuppressed()) Start();
        else
        {
            using var suppressed = ExecutionContext.SuppressFlow();
            Start();
        }

        void Start()
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _heartbeats = completed.Task;
            var heartbeat = new Thread(() =>
            {
                try { HeartbeatLoop(_stopping.Token); completed.TrySetResult(); }
                catch (Exception error) { RecordHeartbeatFailure(error); completed.TrySetException(error); }
            }) { IsBackground = true, Name = "Sunder App policy heartbeat" };
            heartbeat.Start();
            _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "Sunder App policy watchdog" };
            _watchdog.Start();
        }
    }

    private void HeartbeatLoop(CancellationToken token)
    {
        using var activity = AppPolicySchedulingActivity.Begin();
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                SetHeartbeatPhase("handshake");
                var handshake = _connection.HeartbeatHandshake(deadline.Token);
                ObserveRuntimeIdentity(handshake.RuntimeInstanceId);
                if (handshake.RuntimeInstanceId != _session!.RuntimeInstanceId) { FailFast("runtime-instance-changed"); return; }
                SetHeartbeatPhase("status");
                var status = _connection.Heartbeat(_session, deadline.Token);
                ObserveSessionStatus(status);
                if (status.SessionId != _session.SessionId) { FailFast("session-identity-changed"); return; }
                if (status.ShutdownRequired || status.State != RuntimeAppPolicySessionStates.Active)
                {
                    if (Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
                    {
                        try
                        {
                            WriteDiagnostic("shutdown-requested");
                        }
                        finally
                        {
                            // Diagnostic failures must not prevent ordered shutdown.
                            _shutdownApp();
                        }
                    }
                }
                else
                {
                    lock (_proofGate) { if (_shutdownRequested == 0) UpdateProof(); }
                    RecordHeartbeatSuccess();
                    ObserveRetirements(status.Retirements);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                // Failed attempts never renew proof. Record only bounded metadata;
                // the independent native-clock watchdog still owns the fixed grace.
                RecordHeartbeatFailure(error);
            }
            SetHeartbeatPhase("delay");
            if (WaitForNextHeartbeat(token)) return;
        }
    }

    private bool WaitForNextHeartbeat(CancellationToken token)
    {
        // The system path does not use a managed timer or a ThreadPool continuation.
        if (ReferenceEquals(_time, TimeProvider.System)) return token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
        using var elapsed = new ManualResetEvent(false);
        using var timer = _time.CreateTimer(static state => ((EventWaitHandle)state!).Set(),
            elapsed, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        return WaitHandle.WaitAny([elapsed, token.WaitHandle]) == 1;
    }

    private void WatchdogLoop()
    {
        while (_failed == 0)
        {
            bool armed;
            lock (_proofGate) armed = _armed;
            if (_stopping.IsCancellationRequested && !armed) return;
            CheckWatchdog();
            Thread.Sleep(100);
        }
    }

    private void ObserveRetirements(IReadOnlyList<RuntimeAppActivationRetirementRequirement> requirements)
    {
        var owned = requirements.Where(OwnsRetirement).ToArray();
        if (owned.Length == 0 || _stopping.IsCancellationRequested) return;
        lock (_retirementGate)
        {
            if (!_retirements.IsCompleted || _retirementHandler is not { } handler) return;
            // Cleanup may block on package code; it must never occupy heartbeat proof.
            if (ExecutionContext.IsFlowSuppressed()) _retirements = Task.Run(RetireAsync);
            else { using (ExecutionContext.SuppressFlow()) _retirements = Task.Run(RetireAsync); }
            async Task RetireAsync()
            {
                try { await handler(owned, _stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
                catch (Exception error) when (AppPackageUiFaultPolicy.IsRecoverable(error))
                {
                    AppSessionLog.WriteError("An App activation retirement request could not be acknowledged.", error);
                }
            }
        }
    }

    internal void CheckWatchdog()
    {
        var reason = "heartbeat-grace-expired";
        lock (_proofGate)
        {
            if (!_armed) return;
            try
            {
                var now = _clock();
                if (_lastProof is not null && now.SameEpoch(_lastProof) && now.Ticks >= _lastProof.Ticks
                    && (Int128)now.Ticks - _lastProof.Ticks < (Int128)now.Frequency * RuntimePolicyTiming.AppHeartbeatGraceSeconds) return;
                if (_lastProof is null) reason = "missing-proof";
                else if (!now.SameEpoch(_lastProof)) reason = "clock-epoch-changed";
                else if (now.Ticks < _lastProof.Ticks) reason = "clock-regressed";
            }
            catch (Exception) { reason = "clock-read-failed"; }
        }
        FailFast(reason);
    }

    private void FailFast(string reason)
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0)
        {
            var diagnostic = DescribeSupervision(reason);
            TryWriteDiagnostic(diagnostic);
            // The file queue may not drain before termination. Include the same
            // credential-free evidence in the native fail-fast report/stderr.
            _failFast($"{PackagePolicyErrorCodes.AppProcessSupervisionRequired}; {diagnostic}");
        }
    }

    private void UpdateProof()
    {
        var now = _clock();
        // A response racing the watchdog after resume must not revive an expired grace.
        if (_armed && (_lastProof is not { } previous || !now.SameEpoch(previous) || now.Ticks < previous.Ticks
            || (Int128)now.Ticks - previous.Ticks >= (Int128)now.Frequency * RuntimePolicyTiming.AppHeartbeatGraceSeconds))
        {
            FailFast("late-proof");
            throw new InvalidOperationException(PackagePolicyErrorCodes.AppProcessSupervisionRequired);
        }
        _lastProof = now;
    }

    private void ValidateLease(RuntimePolicyLeaseStatus lease)
    {
        var now = _clock();
        var evidence = lease.Clock;
        if (lease.Watermark is not { PackagePolicyRevision: > 0 }
            || lease.ResponseNonce is not { Length: 32 } || lease.ResponseSha256 is not { Length: 64 }
            || lease.ObservedAtUtc is null || lease.ValidUntilUtc - lease.ObservedAtUtc != TimeSpan.FromSeconds(RuntimePolicyTiming.LeaseSeconds)
            || evidence is null || evidence.BootSessionId != now.BootSessionId || evidence.ClockSource != now.Source || evidence.TicksPerSecond != now.Frequency
            || (Int128)evidence.DeadlineTicks - evidence.RequestStartedTicks != (Int128)now.Frequency * RuntimePolicyTiming.LeaseSeconds
            || now.Ticks < evidence.RequestStartedTicks || now.Ticks < evidence.HighWaterTicks || now.Ticks >= evidence.DeadlineTicks)
            throw new InvalidOperationException(PackagePolicyErrorCodes.AppProcessSupervisionRequired);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _connection.ShutdownAsync(_session, timeout.Token).ConfigureAwait(false); }
            catch (Exception error) { RecordHeartbeatFailure(error, "shutdown", "shutdown-request-failed"); }
        }
        _stopping.Cancel();
        try { await _heartbeats.ConfigureAwait(false); } catch (OperationCanceledException) { }
        SetHeartbeatPhase("stopped");
        // Once package code was permitted, disposal cannot disable the watchdog.
        // The process exits normally, or the fixed grace still ends in fail-fast.
    }

    private sealed class Connection(RuntimeManagementClient client, AppRuntimeProcessTransport transport) : IAppPolicyConnection
    {
        public string? DiagnosticPhase => transport.DiagnosticPhase;
        public Task<RuntimeHandshakeResponse> HandshakeAsync(CancellationToken token) => transport.HandshakeAsync(token);
        public RuntimeHandshakeResponse HeartbeatHandshake(CancellationToken token) => transport.HeartbeatHandshake(token);
        public Task<RuntimeAppPolicySessionDescriptor> OpenAsync(RuntimeAppPolicySessionOpenRequest request, CancellationToken token) => client.OpenAppPolicySessionAsync(request, token);
        public RuntimeAppPolicySessionStatus Heartbeat(RuntimeAppPolicySessionRequest request, CancellationToken token) => transport.Heartbeat(request, token);
        public Task<RuntimeAppPackageLoadPermitResult> PermitAsync(RuntimeAppPackageLoadPermitRequest request, CancellationToken token) => client.GetAppPackageLoadPermitAsync(request, token);
        public Task<RuntimeAppActivationRetirementResult> RetireAsync(RuntimeAppActivationRetirementRequest request, CancellationToken token) => client.RetireAppActivationAsync(request, token);
        public Task<RuntimeAppPolicySessionStatus> ShutdownAsync(RuntimeAppPolicySessionRequest request, CancellationToken token) => client.ShutdownAppPolicySessionAsync(request, token);
    }
}
