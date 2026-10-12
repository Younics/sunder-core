using System.Diagnostics;
using System.Reflection;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.App.Services;

internal interface IAppPackagePolicy
{
    Task RequireLoadAsync(PackageUiSnapshotDescriptor snapshot, CancellationToken token);
}

internal sealed class UnavailableAppPackagePolicy : IAppPackagePolicy
{
    internal static UnavailableAppPackagePolicy Instance { get; } = new();
    public Task RequireLoadAsync(PackageUiSnapshotDescriptor snapshot, CancellationToken token)
        => Task.FromException(new InvalidOperationException(PackagePolicyErrorCodes.AppProcessSupervisionRequired));
}

internal interface IAppPolicyConnection
{
    Task<RuntimeHandshakeResponse> HandshakeAsync(CancellationToken token);
    Task<RuntimeAppPolicySessionDescriptor> OpenAsync(RuntimeAppPolicySessionOpenRequest request, CancellationToken token);
    Task<RuntimeAppPolicySessionStatus> HeartbeatAsync(RuntimeAppPolicySessionRequest request, CancellationToken token);
    Task<RuntimeAppPackageLoadPermitResult> PermitAsync(RuntimeAppPackageLoadPermitRequest request, CancellationToken token);
    Task<RuntimeAppPolicySessionStatus> ShutdownAsync(RuntimeAppPolicySessionRequest request, CancellationToken token);
}

internal sealed class AppProcessPolicySupervisor : IAppPackagePolicy, IAsyncDisposable
{
    private readonly IAppPolicyConnection _connection;
    private readonly Func<Guid, RuntimeAppProcessIdentity> _identity;
    private readonly Action _shutdownApp;
    private readonly Action<string> _failFast;
    private readonly Func<RuntimeContinuousClockReading> _clock;
    private readonly TimeProvider _time;
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

    internal AppProcessPolicySupervisor(AppRuntimeProcessTransport transport, Action shutdownApp)
        : this(new Connection(new RuntimeManagementClient(transport.Transport), transport.HandshakeAsync), runtimeId =>
        {
            using var process = Process.GetCurrentProcess();
            return new(process.Id, RuntimeProcessIdentity.Birth(process), RuntimeProcessIdentity.ExecutableHash(process),
                RuntimeProcessIdentity.BuildIdentity(typeof(AppProcessPolicySupervisor).Assembly), Guid.NewGuid(), runtimeId);
        }, shutdownApp, Environment.FailFast, RuntimeContinuousClock.Read, TimeProvider.System) { }

    internal AppProcessPolicySupervisor(IAppPolicyConnection connection, Func<Guid, RuntimeAppProcessIdentity> identity,
        Action shutdownApp, Action<string> failFast, Func<RuntimeContinuousClockReading> clock, TimeProvider time)
    { _connection = connection; _identity = identity; _shutdownApp = shutdownApp; _failFast = failFast; _clock = clock; _time = time; }

    public async Task RequireLoadAsync(PackageUiSnapshotDescriptor snapshot, CancellationToken token)
    {
        if (snapshot.PolicySubject is null || snapshot.SnapshotId is not { Length: 32 }) throw new InvalidOperationException(PackagePolicyErrorCodes.AppProcessSupervisionRequired);
        await _openGate.WaitAsync(token);
        try
        {
            if (_failed != 0 || _shutdownRequested != 0 || _stopping.IsCancellationRequested) throw new InvalidOperationException(PackagePolicyErrorCodes.AppProcessTerminationRequired);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            if (_session is null)
            {
                var handshake = await _connection.HandshakeAsync(timeout.Token);
                var identity = _identity(handshake.RuntimeInstanceId);
                var opened = await _connection.OpenAsync(new(identity), timeout.Token);
                if (opened.Process != identity || opened.State != RuntimeAppPolicySessionStates.Active) throw new InvalidDataException("App supervision identity differs.");
                _session = new(opened.SessionId, identity.AppGenerationId, identity.RuntimeInstanceId);
                _heartbeats = HeartbeatLoopAsync(_stopping.Token);
                _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "Sunder App policy watchdog" };
                _watchdog.Start();
            }
            var permit = await _connection.PermitAsync(new(_session, snapshot.PolicySubject) { SnapshotId = snapshot.SnapshotId }, timeout.Token);
            if (!permit.Allowed || permit.Session != _session || permit.Package != snapshot.PolicySubject || string.IsNullOrWhiteSpace(permit.PermitId)
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
        }
        finally { _openGate.Release(); }
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var handshake = await _connection.HandshakeAsync(deadline.Token);
                if (handshake.RuntimeInstanceId != _session!.RuntimeInstanceId) { FailFast(); return; }
                var status = await _connection.HeartbeatAsync(_session, deadline.Token);
                if (status.SessionId != _session.SessionId) { FailFast(); return; }
                if (status.ShutdownRequired || status.State != RuntimeAppPolicySessionStates.Active)
                {
                    if (Interlocked.Exchange(ref _shutdownRequested, 1) == 0) _shutdownApp();
                }
                else lock (_proofGate) { if (_shutdownRequested == 0) UpdateProof(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception) { /* The independent native-clock watchdog owns the fixed grace. */ }
            await Task.Delay(TimeSpan.FromSeconds(1), _time, token);
        }
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

    internal void CheckWatchdog()
    {
        lock (_proofGate)
        {
            if (!_armed) return;
            try
            {
                var now = _clock();
                if (_lastProof is not null && now.SameEpoch(_lastProof) && now.Ticks >= _lastProof.Ticks
                    && (Int128)now.Ticks - _lastProof.Ticks < (Int128)now.Frequency * RuntimePolicyTiming.AppHeartbeatGraceSeconds) return;
            }
            catch (Exception) { }
        }
        FailFast();
    }

    private void FailFast()
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0) _failFast(PackagePolicyErrorCodes.AppProcessSupervisionRequired);
    }

    private void UpdateProof()
    {
        var now = _clock();
        // A response racing the watchdog after resume must not revive an expired grace.
        if (_armed && (_lastProof is not { } previous || !now.SameEpoch(previous) || now.Ticks < previous.Ticks
            || (Int128)now.Ticks - previous.Ticks >= (Int128)now.Frequency * RuntimePolicyTiming.AppHeartbeatGraceSeconds))
        {
            FailFast();
            throw new InvalidOperationException(PackagePolicyErrorCodes.AppProcessSupervisionRequired);
        }
        _lastProof = now;
    }

    private void ValidateLease(RuntimePolicyLeaseStatus lease)
    {
        var now = _clock();
        var evidence = lease.Clock;
        if (lease.Watermark is not { PackagePolicyRevision: > 0, CheckpointSequence: > 0, TopologyGeneration: > 0 }
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
            try { await _connection.ShutdownAsync(_session, timeout.Token); } catch (Exception) { }
        }
        _stopping.Cancel();
        try { await _heartbeats; } catch (OperationCanceledException) { }
        // Once package code was permitted, disposal cannot disable the watchdog.
        // The process exits normally, or the fixed grace still ends in fail-fast.
    }

    private sealed class Connection(RuntimeManagementClient client, Func<CancellationToken, Task<RuntimeHandshakeResponse>> handshake) : IAppPolicyConnection
    {
        public Task<RuntimeHandshakeResponse> HandshakeAsync(CancellationToken token) => handshake(token);
        public Task<RuntimeAppPolicySessionDescriptor> OpenAsync(RuntimeAppPolicySessionOpenRequest request, CancellationToken token) => client.OpenAppPolicySessionAsync(request, token);
        public Task<RuntimeAppPolicySessionStatus> HeartbeatAsync(RuntimeAppPolicySessionRequest request, CancellationToken token) => client.HeartbeatAppPolicySessionAsync(request, token);
        public Task<RuntimeAppPackageLoadPermitResult> PermitAsync(RuntimeAppPackageLoadPermitRequest request, CancellationToken token) => client.GetAppPackageLoadPermitAsync(request, token);
        public Task<RuntimeAppPolicySessionStatus> ShutdownAsync(RuntimeAppPolicySessionRequest request, CancellationToken token) => client.ShutdownAppPolicySessionAsync(request, token);
    }
}
