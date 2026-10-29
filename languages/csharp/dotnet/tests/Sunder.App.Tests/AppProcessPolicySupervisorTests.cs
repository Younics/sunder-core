using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Compatibility;
using Xunit;

namespace Sunder.App.Tests;

public sealed partial class AppProcessPolicySupervisorTests
{
    private static readonly AsyncLocal<string?> ActivationContext = new();

    [Fact]
    public async Task MissingSupervisionFailsClosedBeforeAnyLoad()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => UnavailableAppPackagePolicy.Instance.RequireLoadAsync(Snapshot(), default));
    }

    [Fact]
    public async Task SessionIsOpenedBeforePermitAndIsReusedAcrossUiGenerations()
    {
        await using var fixture = new Fixture();
        await fixture.Supervisor.RequireLoadAsync(Snapshot(), default);
        await fixture.Supervisor.RequireLoadAsync(Snapshot() with { SessionGeneration = 2 }, default);
        Assert.Equal(1, fixture.Connection.Opens);
        Assert.Equal(2, fixture.Connection.Permits);
        Assert.Equal(0, fixture.Failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImplementedObservationCapabilitiesAreSentOnlyForTheNewManagedAppTargets(bool observations)
    {
        await using var fixture = new Fixture();
        var snapshot = Snapshot();
        if (observations)
        {
            var target = snapshot.Target;
            snapshot = snapshot with { Target = new(target.Role, target.Rid, "avalonia", target.EntryPoint,
                target.TargetFramework, target.SdkVersion,
                [SunderSdkCapabilities.Baseline11V1, SunderSdkCapabilities.WorkerExecutionObservationsV1,
                    SunderSdkCapabilities.PackageDevelopmentRevisionsV1], target.Views, target.ExecutionRuntime, target.ExecutionRuntimeBinding) };
        }
        await fixture.Supervisor.AcquireLoadAsync(snapshot, Guid.NewGuid(), default);
        var request = fixture.Connection.LastPermit!;
        var json = JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (!observations)
        {
            Assert.Null(request.AppImplementedCapabilities);
            Assert.False(json.TryGetProperty("appImplementedCapabilities", out _));
        }
        else
        {
            Assert.Equal(new[] { SunderSdkCapabilities.WorkerExecutionObservationsV1,
                SunderSdkCapabilities.PackageDevelopmentRevisionsV1 }, request.AppImplementedCapabilities);
            var claims = new[] { SunderSdkCapabilities.WorkerExecutionObservationsV1 };
            var frozen = request with { AppImplementedCapabilities = claims };
            claims[0] = "modified";
            Assert.Equal(SunderSdkCapabilities.WorkerExecutionObservationsV1, Assert.Single(frozen.AppImplementedCapabilities!));
            Assert.Equal(request.AppImplementedCapabilities,
                json.Deserialize<RuntimeAppPackageLoadPermitRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AppImplementedCapabilities);
        }
    }

    [Theory]
    [InlineData("dotnet", false, true)]
    [InlineData("web", false, true)]
    [InlineData("dotnet", true, true)]
    [InlineData("dotnet", false, false)]
    [InlineData("web", false, false)]
    [InlineData("dotnet", true, false)]
    public async Task ActivationRetirementRequiresExactAcknowledgementAndKeepsSupervision(string kind, bool wrongAcknowledgement, bool development)
    {
        await using var fixture = new Fixture();
        fixture.Connection.Development = development;
        var snapshot = Snapshot() with { SourceKind = development ? PackageSourceKind.Dev : PackageSourceKind.Installed, Target = Snapshot().Target with { Kind = kind } };
        var activationId = Guid.NewGuid();
        var admission = await fixture.Supervisor.AcquireLoadAsync(snapshot, activationId, default);
        Assert.Equal(activationId, admission.ActivationId);
        Assert.Equal(snapshot.Target, fixture.Connection.LastPermit!.AppTarget);
        var requirement = new RuntimeAppActivationRetirementRequirement(fixture.Connection.SessionId, activationId, admission.PermitId!, snapshot.PolicySubject!);
        Assert.False(fixture.Supervisor.HasRetiredActivation(requirement));
        fixture.Connection.WrongRetirement = wrongAcknowledgement;
        if (wrongAcknowledgement)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => admission.RetireAsync());
            Assert.False(fixture.Supervisor.HasRetiredActivation(requirement));
            fixture.Connection.WrongRetirement = false;
        }
        await admission.RetireAsync();
        await admission.RetireAsync();
        Assert.Equal(wrongAcknowledgement ? 2 : 1, fixture.Connection.Retirements.Count);
        Assert.True(fixture.Supervisor.HasRetiredActivation(requirement));
        Assert.False(fixture.Supervisor.HasRetiredActivation(requirement with { PolicySessionId = "foreign" }));
        Assert.False(fixture.Supervisor.HasRetiredActivation(requirement with { AppActivationId = Guid.NewGuid() }));
        Assert.All(fixture.Connection.Retirements, request => Assert.Equal(admission.PermitId, request.LoadPermitId));
        await fixture.WaitForHeartbeatIdleAsync();
        fixture.Now = fixture.Now with { Ticks = 15000 };
        fixture.Supervisor.CheckWatchdog();
        Assert.Equal(1, fixture.Failures); // Retirement never removes loaded security supervision.
    }

    [Fact]
    public async Task RequestedActivationRetirementCannotBlockHeartbeatOrInheritPackageContext()
    {
        await using var fixture = new Fixture();
        fixture.Connection.Development = true;
        var admission = await fixture.Supervisor.AcquireLoadAsync(Snapshot() with { SourceKind = PackageSourceKind.Dev }, Guid.NewGuid(), default);
        var requirement = new RuntimeAppActivationRetirementRequirement(fixture.Connection.SessionId, admission.ActivationId, admission.PermitId!, Snapshot().PolicySubject!);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Supervisor.SetRetirementHandler(async (requirements, token) =>
        {
            Assert.Equal(requirement, Assert.Single(requirements));
            Assert.Null(ActivationContext.Value);
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped.TrySetResult(); }
        });
        fixture.Connection.RequiredRetirements = [requirement, requirement with { PolicySessionId = "another-app" }];
        await fixture.NextHeartbeatAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var before = fixture.Connection.Heartbeats;
        await fixture.NextHeartbeatAsync();
        Assert.True(fixture.Connection.Heartbeats > before);
        Assert.Equal(0, fixture.Failures);
        await fixture.StopSupervisorAsync();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("regression")]
    [InlineData("boot")]
    public async Task NativeClockWatchdogFailsFastOnSupervisionLoss(string failure)
    {
        await using var fixture = new Fixture();
        await fixture.Supervisor.RequireLoadAsync(Snapshot(), default);
        await fixture.WaitForHeartbeatIdleAsync();
        fixture.Now = failure switch
        {
            "expiry" => fixture.Now with { Ticks = fixture.Now.Ticks + 5000 },
            "regression" => fixture.Now with { Ticks = fixture.Now.Ticks - 1 },
            _ => fixture.Now with { BootSessionId = "changed" },
        };
        fixture.Supervisor.CheckWatchdog();
        await fixture.FailureObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, fixture.Failures);
    }

    [Fact]
    public async Task WrongPermitIdentityCannotAuthorizeLoading()
    {
        await using var fixture = new Fixture();
        fixture.Connection.WrongIdentity = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.RequireLoadAsync(Snapshot(), default));
    }

    [Fact]
    public async Task DevelopmentPermitCannotAuthorizeAnInstalledPackage()
    {
        await using var fixture = new Fixture();
        fixture.Connection.Development = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.RequireLoadAsync(Snapshot(), default));
        await fixture.Supervisor.RequireLoadAsync(Snapshot() with { SourceKind = PackageSourceKind.Dev }, default);
    }

    [Fact]
    public async Task DevelopmentImportPermitCanCarryAValidatedPlatformLease()
    {
        await using var fixture = new Fixture();
        fixture.Connection.Development = true; fixture.Connection.DevelopmentPlatformLease = true;
        await fixture.Supervisor.RequireLoadAsync(Snapshot() with { SourceKind = PackageSourceKind.Dev }, default);
        await fixture.WaitForHeartbeatIdleAsync();
        Assert.Equal(0, fixture.Failures);
        fixture.Now = fixture.Now with { Ticks = 86_410_000 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.RequireLoadAsync(Snapshot() with { SourceKind = PackageSourceKind.Dev }, default));
    }

    [Fact]
    public async Task LatePermitCannotReviveExpiredSupervisionBeforeTheWatchdogRuns()
    {
        await using var fixture = new Fixture();
        await fixture.Supervisor.RequireLoadAsync(Snapshot(), default);
        await fixture.WaitForHeartbeatIdleAsync();
        fixture.Connection.BeforePermit = () => fixture.Now = fixture.Now with { Ticks = fixture.Now.Ticks + 5000 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.RequireLoadAsync(Snapshot(), default));
        Assert.Equal(1, fixture.Failures);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.RequireLoadAsync(Snapshot(), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PolicyShutdownIsRequestedOnceAndCannotExtendTheWatchdogGrace(bool disposeDuringCleanup)
    {
        await using var fixture = new Fixture();
        await fixture.Supervisor.RequireLoadAsync(Snapshot(), default);
        await fixture.WaitForHeartbeatIdleAsync();
        var lastProof = fixture.Now;
        fixture.Now = lastProof with { Ticks = lastProof.Ticks + 1000 };
        fixture.Connection.HeartbeatState = RuntimeAppPolicySessionStates.ShutdownRequested;
        fixture.Connection.ShutdownRequired = true;

        await fixture.NextHeartbeatAsync();

        Assert.Equal(1, Volatile.Read(ref fixture.ShutdownRequests));
        var denial = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Supervisor.RequireLoadAsync(Snapshot(), default));
        Assert.Equal(PackagePolicyErrorCodes.AppProcessTerminationRequired, denial.Message);
        Assert.Equal(1, fixture.Connection.Permits);
        fixture.Now = lastProof with { Ticks = lastProof.Ticks + 2000 };
        await fixture.NextHeartbeatAsync();
        Assert.Equal(1, Volatile.Read(ref fixture.ShutdownRequests));

        // Even a later "active" response cannot revive proof after shutdown was
        // requested. Ordered UI cleanup may still be running at this point.
        fixture.Connection.HeartbeatState = RuntimeAppPolicySessionStates.Active;
        fixture.Connection.ShutdownRequired = false;
        fixture.Now = lastProof with { Ticks = lastProof.Ticks + 3000 };
        await fixture.NextHeartbeatAsync();
        if (disposeDuringCleanup)
        {
            await fixture.StopSupervisorAsync();
            Assert.Equal(1, fixture.Connection.Shutdowns);
        }

        var deadline = lastProof.Ticks + lastProof.Frequency * RuntimePolicyTiming.AppHeartbeatGraceSeconds;
        fixture.Now = lastProof with { Ticks = deadline - 1 };
        fixture.Supervisor.CheckWatchdog();
        Assert.Equal(0, Volatile.Read(ref fixture.Failures));
        fixture.Now = lastProof with { Ticks = deadline };
        fixture.Supervisor.CheckWatchdog();
        fixture.Supervisor.CheckWatchdog();
        await fixture.FailureObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref fixture.Failures));
        Assert.Equal(1, Volatile.Read(ref fixture.ShutdownRequests));
    }

    [Fact]
    public async Task PolicyShutdownStillRunsWhenTheDiagnosticSinkThrows()
    {
        await using var fixture = new Fixture();
        await fixture.Supervisor.RequireLoadAsync(Snapshot(), default);
        var observed = 0;
        fixture.DiagnosticObserver = message =>
        {
            if (!message.Contains("event=shutdown-requested;", StringComparison.Ordinal)) return;
            Interlocked.Increment(ref observed);
            throw new InvalidOperationException("diagnostic sink failed");
        };
        fixture.Connection.HeartbeatState = RuntimeAppPolicySessionStates.ShutdownRequested;
        fixture.Connection.ShutdownRequired = true;

        await fixture.NextHeartbeatAsync();

        Assert.Equal(1, Volatile.Read(ref observed));
        Assert.Equal(1, Volatile.Read(ref fixture.ShutdownRequests));
        Assert.Equal(0, Volatile.Read(ref fixture.Failures));
    }

    [Fact]
    public async Task BlockedUiAndLongGenerationLoadsDoNotOwnHeartbeatSchedulingOrContext()
    {
        await using var fixture = new Fixture();
        var ui = new BlockedSynchronizationContext();
        TaskCompletionSource? releasePermit = null;
        try
        {
            await FromUi(() => fixture.Supervisor.RequireLoadAsync(Snapshot(), default))
                .WaitAsync(TimeSpan.FromSeconds(2));
            await fixture.WaitForHeartbeatIdleAsync();

            for (var generation = 2; generation <= 3; generation++)
            {
                var permitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                releasePermit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = releasePermit;
                fixture.Connection.DuringPermit = async (_, token) =>
                {
                    permitStarted.TrySetResult();
                    await release.Task.WaitAsync(token).ConfigureAwait(false);
                };
                var loading = FromUi(() => fixture.Supervisor.RequireLoadAsync(
                    Snapshot() with { SessionGeneration = generation }, default));
                await permitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

                // Keep both the UI dispatcher and package load occupied for more
                // than one native-clock grace, while real heartbeat work proceeds.
                for (var tick = 0; tick <= RuntimePolicyTiming.AppHeartbeatGraceSeconds; tick++)
                {
                    fixture.Now = fixture.Now with { Ticks = fixture.Now.Ticks + fixture.Now.Frequency };
                    await fixture.NextHeartbeatAsync();
                    fixture.Supervisor.CheckWatchdog();
                    Assert.Equal(0, Volatile.Read(ref fixture.Failures));
                }
                Assert.False(loading.IsCompleted);
                releasePermit.SetResult();
                await loading.WaitAsync(TimeSpan.FromSeconds(2));
            }

            Assert.Equal(1, fixture.Connection.Opens);
            Assert.Equal(3, fixture.Connection.Permits);
            Assert.Equal(0, ui.Posts);
            Assert.All(fixture.Connection.HeartbeatContexts, observed =>
            {
                Assert.Null(observed.Context);
                Assert.Null(observed.Activation);
            });
        }
        finally
        {
            releasePermit?.TrySetResult();
            ui.Release();
        }

        Task FromUi(Func<Task> work)
        {
            var previousContext = SynchronizationContext.Current;
            var previousActivation = ActivationContext.Value;
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                ActivationContext.Value = "completed-package-activation-parent";
                return work();
            }
            finally
            {
                ActivationContext.Value = previousActivation;
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("session")]
    public async Task BackgroundHeartbeatsStillFailClosedOnIdentityReplacement(string replacement)
    {
        await using var fixture = new Fixture();
        await fixture.Supervisor.RequireLoadAsync(Snapshot(), default);
        await fixture.WaitForHeartbeatIdleAsync();
        if (replacement == "runtime")
            fixture.Connection.Handshake = fixture.Connection.Handshake with { RuntimeInstanceId = Guid.NewGuid() };
        else fixture.Connection.HeartbeatSessionId = "untrusted-session-secret";

        await fixture.NextHeartbeatAsync();
        await fixture.FailureObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, Volatile.Read(ref fixture.Failures));
        Assert.Equal(0, Volatile.Read(ref fixture.ShutdownRequests));
        var diagnostic = Assert.Single(fixture.FailureMessages);
        Assert.Contains(replacement == "runtime" ? "event=runtime-instance-changed;" : "event=session-identity-changed;", diagnostic);
        Assert.DoesNotContain("untrusted-session-secret", diagnostic);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.RequireLoadAsync(Snapshot(), default));
        Assert.Equal(1, fixture.Connection.Permits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeartbeatDiagnosticsAreCorrelatedBoundedAndRetainFailureEvidenceAfterRecovery(bool socketFailure)
    {
        await using var fixture = new Fixture();
        await fixture.Supervisor.RequireLoadAsync(Snapshot(), default);
        await fixture.WaitForHeartbeatIdleAsync();
        fixture.Connection.HeartbeatFailure = socketFailure
            ? new IOException("Bearer private-worker-secret", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset))
            : new RuntimeClientException(HttpStatusCode.ServiceUnavailable,
                "Bearer private-worker-secret", "http://private/path?token=private-worker-secret", "private-worker-secret");
        if (socketFailure) RuntimePolicyTransportFailure.Capture(fixture.Connection.HeartbeatFailure,
            RuntimePolicyTransportPhase.ReadHeaders, RuntimePolicyTransportKind.UnixSocket, System.Diagnostics.Stopwatch.GetTimestamp());
        var nativeSocketError = RuntimePolicyTransportFailure.From(fixture.Connection.HeartbeatFailure)?.NativeError;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            fixture.Now = fixture.Now with { Ticks = fixture.Now.Ticks + 1000 };
            await fixture.NextHeartbeatAsync();
        }
        Assert.Single(fixture.Diagnostics, message => message.Contains("event=heartbeat-failed;", StringComparison.Ordinal));
        fixture.Connection.HeartbeatFailure = null;
        fixture.Now = fixture.Now with { Ticks = fixture.Now.Ticks + 1000 };
        await fixture.NextHeartbeatAsync();
        Assert.Single(fixture.Diagnostics, message => message.Contains("event=heartbeat-recovered;", StringComparison.Ordinal));
        Assert.Equal(0, Volatile.Read(ref fixture.Failures));

        fixture.Now = fixture.Now with { Ticks = fixture.Now.Ticks + fixture.Now.Frequency * RuntimePolicyTiming.AppHeartbeatGraceSeconds };
        fixture.Supervisor.CheckWatchdog();
        await fixture.FailureObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var terminal = Assert.Single(fixture.FailureMessages);
        Assert.Contains("event=heartbeat-grace-expired;", terminal);
        Assert.Contains($"policy session={fixture.Connection.SessionId};", terminal);
        Assert.Contains($"Runtime instance={fixture.Connection.Handshake.RuntimeInstanceId};", terminal);
        Assert.Contains("phase=delay/connection;", terminal);
        Assert.Contains("lastFailurePhase=status/connection;", terminal);
        Assert.Contains(socketFailure ? "lastFailureType=IOException;" : "lastFailureType=RuntimeClientException;", terminal);
        Assert.Contains(socketFailure ? "socketError=ConnectionReset;" : "lastFailureHttpStatus=503;", terminal);
        if (socketFailure)
        {
            Assert.Contains("transportPhase=ReadHeaders;", terminal);
            Assert.Contains("transportKind=UnixSocket;", terminal);
            Assert.Contains($"nativeSocketError={nativeSocketError};", terminal);
        }
        Assert.Contains("proofAgeMs=5000;", terminal);
        Assert.DoesNotContain("lastSuccessUtc=;", terminal);
        Assert.DoesNotContain("lastFailureUtc=;", terminal);
        Assert.All(fixture.Diagnostics.Append(terminal), message =>
        {
            Assert.True(message.Length <= 2200);
            Assert.DoesNotContain("private-worker-secret", message);
            Assert.DoesNotContain("http://", message);
            Assert.DoesNotContain("Bearer", message);
        });
    }

    [Fact]
    public void PolicyDiagnosticsDoNotCallSynchronousLogSubscribers()
    {
        var marker = $"policy-diagnostic-{Guid.NewGuid():N}";
        var callbacks = 0;
        void Observe(AppSessionLogSnapshotEntry entry)
        {
            if (entry.Message == marker) Interlocked.Increment(ref callbacks);
        }
        AppSessionLog.EntryWritten += Observe;
        try
        {
            AppSessionLog.WritePolicySupervisionDiagnostic(marker);
            Assert.Equal(0, Volatile.Read(ref callbacks));
        }
        finally { AppSessionLog.EntryWritten -= Observe; }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("session")]
    public async Task RetainedObservationPermitRechecksExactActivationTargetAndCapabilities(string? mismatch)
    {
        await using var fixture = new Fixture();
        var snapshot = Snapshot();
        var target = snapshot.Target;
        snapshot = snapshot with { Target = new(target.Role, target.Rid, target.Kind, target.EntryPoint,
            target.TargetFramework, target.SdkVersion, [SunderSdkCapabilities.PackageDevelopmentRevisionsV1,
                SunderSdkCapabilities.WorkerExecutionObservationsV1], target.Views) };
        var admission = await fixture.Supervisor.AcquireLoadAsync(snapshot, Guid.NewGuid(), default);
        var retained = mismatch is null ? admission : new AppPackageLoadAdmission(admission.ActivationId, admission.PermitId)
        { PolicySessionId = mismatch == "session" ? "foreign-session" : admission.PolicySessionId };
        var fresh = snapshot with { SessionGeneration = 2, SnapshotId = new('b', 32) };
        if (mismatch is null) await fixture.Supervisor.RequireRetainedLoadAsync(fresh, retained, default);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.RequireRetainedLoadAsync(fresh, retained, default));
        var request = Assert.IsType<RuntimeAppPackageLoadPermitRequest>(fixture.Connection.LastPermit);
        Assert.Equal(admission.ActivationId, request.AppActivationId);
        Assert.Same(fresh.Target, request.AppTarget);
        Assert.Equal(fresh.SnapshotId, request.SnapshotId);
        Assert.Equal(fresh.Target.RequiredHostCapabilities, request.AppImplementedCapabilities);
        Assert.Empty(fixture.Connection.Retirements);
    }

    private static PackageUiSnapshotDescriptor Snapshot() => new("example.app", PackageSourceKind.Installed, 1, RuntimeContractTestData.AppTarget(), new('a', 64), new('a', 32), "")
    { PolicySubject = new("example.app", "1.0.0", new('a', 64)) };

    private sealed class Fixture : IAsyncDisposable
    {
        private RuntimeContinuousClockReading _now = new("boot", "test", 1000, 10000);
        private readonly ManualTimeProvider _time = new();
        private Task? _disposeTask;
        internal RuntimeContinuousClockReading Now
        {
            get => Volatile.Read(ref _now);
            set => Volatile.Write(ref _now, value);
        }
        internal FakeConnection Connection { get; } = new();
        internal AppProcessPolicySupervisor Supervisor { get; }
        internal TaskCompletionSource FailureObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<string> Diagnostics { get; } = new();
        internal ConcurrentQueue<string> FailureMessages { get; } = new();
        internal Action<string>? DiagnosticObserver;
        internal int Failures;
        internal int ShutdownRequests;
        internal Fixture() => Supervisor = new(Connection, runtime => new(123, "birth", new('b', 64), "build", Guid.NewGuid(), runtime),
            () => Interlocked.Increment(ref ShutdownRequests), message =>
            {
                FailureMessages.Enqueue(message);
                Interlocked.Increment(ref Failures);
                FailureObserved.TrySetResult();
            }, () => Now, _time, message =>
            {
                Diagnostics.Enqueue(message);
                DiagnosticObserver?.Invoke(message);
            });

        internal async Task WaitForHeartbeatIdleAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while ((Volatile.Read(ref Connection.Heartbeats) == 0 || _time.PendingTimerCount != 1)
                && Volatile.Read(ref Failures) == 0)
                await Task.Delay(10, timeout.Token);
        }

        internal async Task NextHeartbeatAsync()
        {
            await WaitForHeartbeatIdleAsync();
            var previous = Volatile.Read(ref Connection.Heartbeats);
            _time.Advance(TimeSpan.FromSeconds(1));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            // The next delay is registered only after the heartbeat response has
            // been processed, including its shutdown callback/proof update.
            while ((Volatile.Read(ref Connection.Heartbeats) <= previous || _time.PendingTimerCount != 1)
                && Volatile.Read(ref Failures) == 0)
                await Task.Delay(10, timeout.Token);
        }

        internal Task StopSupervisorAsync() => _disposeTask ??= Supervisor.DisposeAsync().AsTask();

        public async ValueTask DisposeAsync()
        {
            await StopSupervisorAsync();
            Now = Now with { Ticks = Now.Ticks + 10000 };
            Supervisor.CheckWatchdog();
        }
    }

    private sealed class FakeConnection : IAppPolicyConnection
    {
        internal RuntimeHandshakeResponse Handshake = new(RuntimeProtocol.Identity,
            1, 1, 1, Guid.NewGuid(), [], new("Sunder.Runtime.Host", "1.0.0", "1.0.0"));
        internal string SessionId { get; } = Guid.NewGuid().ToString("N");
        internal ConcurrentQueue<(SynchronizationContext? Context, string? Activation)> HeartbeatContexts { get; } = new();
        internal int Opens;
        internal int Permits;
        internal int Heartbeats;
        internal int Shutdowns;
        internal TaskCompletionSource<RuntimeAppPolicySessionStatus>? ShutdownReply;
        internal string HeartbeatState = RuntimeAppPolicySessionStates.Active;
        internal bool ShutdownRequired;
        internal bool WrongIdentity;
        internal bool Development;
        internal bool DevelopmentPlatformLease;
        internal Action? BeforePermit;
        internal Func<Task>? DuringOpen;
        internal Func<RuntimeAppPackageLoadPermitRequest, CancellationToken, Task>? DuringPermit;
        internal Exception? HeartbeatFailure;
        internal string? HeartbeatSessionId;
        internal RuntimeAppPackageLoadPermitRequest? LastPermit;
        internal bool WrongRetirement;
        internal ConcurrentQueue<RuntimeAppActivationRetirementRequest> Retirements { get; } = new();
        internal IReadOnlyList<RuntimeAppActivationRetirementRequirement> RequiredRetirements = [];
        public Task<RuntimeHandshakeResponse> HandshakeAsync(CancellationToken token) => Task.FromResult(Handshake);
        public RuntimeHandshakeResponse HeartbeatHandshake(CancellationToken token) => Handshake;
        public async Task<RuntimeAppPolicySessionDescriptor> OpenAsync(RuntimeAppPolicySessionOpenRequest request, CancellationToken token)
        {
            Opens++;
            if (DuringOpen is { } opening) await opening();
            return new(SessionId, request.Process, "active");
        }
        public RuntimeAppPolicySessionStatus Heartbeat(RuntimeAppPolicySessionRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref Heartbeats);
            HeartbeatContexts.Enqueue((SynchronizationContext.Current, ActivationContext.Value));
            if (HeartbeatFailure is { } failure) throw failure;
            return new RuntimeAppPolicySessionStatus(HeartbeatSessionId ?? request.SessionId, HeartbeatState,
                ShutdownRequired ? PackagePolicyErrorCodes.AppProcessTerminationRequired : null, ShutdownRequired, []) { Retirements = RequiredRetirements };
        }
        public async Task<RuntimeAppPackageLoadPermitResult> PermitAsync(RuntimeAppPackageLoadPermitRequest request, CancellationToken token)
        {
            Assert.True(Opens > 0);
            Permits++;
            LastPermit = request;
            BeforePermit?.Invoke();
            if (DuringPermit is { } work) await work(request, token).ConfigureAwait(false);
            return new RuntimeAppPackageLoadPermitResult(true, null, new('d', 32), request.Session,
                WrongIdentity ? request.Package with { Version = "2.0.0" } : request.Package,
                Development && !DevelopmentPlatformLease ? null : new("valid", null, new("https://registry.example", 1, new('a', 64)),
                    new('d', 32), new('e', 64), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(24), new("boot", "test", 1000, 10000, 86410000, 10000, null))) { Development = Development, AppActivationId = request.AppActivationId };
        }
        public Task<RuntimeAppActivationRetirementResult> RetireAsync(RuntimeAppActivationRetirementRequest request, CancellationToken token)
        {
            Retirements.Enqueue(request);
            return Task.FromResult(new RuntimeAppActivationRetirementResult(request.Session,
                WrongRetirement ? Guid.NewGuid() : request.AppActivationId, request.LoadPermitId, "retired"));
        }
        public Task<RuntimeAppPolicySessionStatus> ShutdownAsync(RuntimeAppPolicySessionRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref Shutdowns);
            return ShutdownReply?.Task ?? Task.FromResult(new RuntimeAppPolicySessionStatus(request.SessionId, "shutdown-requested", PackagePolicyErrorCodes.AppProcessTerminationRequired, true, []));
        }
    }

    private sealed class BlockedSynchronizationContext : SynchronizationContext
    {
        private readonly object _gate = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();
        private bool _released;
        private int _posts;
        internal int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posts);
            lock (_gate)
            {
                if (!_released)
                {
                    _pending.Enqueue((callback, state));
                    return;
                }
            }
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        internal void Release()
        {
            (SendOrPostCallback Callback, object? State)[] pending;
            lock (_gate)
            {
                _released = true;
                pending = _pending.ToArray();
                _pending.Clear();
            }
            foreach (var work in pending) ThreadPool.QueueUserWorkItem(_ => work.Callback(work.State));
        }
    }
}
