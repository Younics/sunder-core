using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class DevPackageOwnerLeaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcknowledgedAppRetirementAllowsLastOwnerUnloadAndPreservesNonLastOwner(bool web)
    {
        using var policy = new ActivePolicyTestContext();
        var process = new RetirementProcess();
        var fixture = CreateFixture(appPolicyFactory: (state, protocol, snapshots, readiness) => new(policy.Paths, protocol, snapshots,
            new(policy.Cache), policy.Cache, policy.Clock, policy.Time, process, NullLogger<AppProcessPolicySessions>.Instance, state, readiness));
        const string token = "retirement-owner-token-aaaaaaaaaaaaaaaa";
        const string secondToken = "retirement-owner-token-bbbbbbbbbbbbbbbb";
        var folder = CreateRequiredAppPackage(fixture.Paths.RootPath, web);
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync("first", Mutation(fixture.Host.RuntimeInstanceId, token, "load", 1, folder, false));
            await fixture.Host.ReplaceDevPackageOwnerAsync("second", Mutation(fixture.Host.RuntimeInstanceId, secondToken, "load", 1, folder, false));
            var (session, permit, caller) = await OpenTrackedAppAsync(fixture.Host);
            var policies = fixture.Host.AppPolicies!;
            var worker = fixture.Host.ActiveSession.LoadedPackageMap["startup.counted.package"].Worker;
            var generation = fixture.Host.SessionGeneration;
            var nonlast = await fixture.Host.StageOwnerReleaseAsync("first", new(fixture.Host.RuntimeInstanceId, token, 1, "release"));
            Assert.Empty(nonlast.Retirements);
            var kept = await fixture.Host.CommitOwnerReleaseAsync("first", new(fixture.Host.RuntimeInstanceId, token, nonlast.StageId!));
            Assert.Equal(generation, kept.Snapshot.SessionGeneration);
            Assert.False(worker.GenerationCompletion.IsCompleted);
            Assert.Single((await fixture.Host.RpcBroker.DiscoverAppAsync(caller.SessionId, "example.rpc", default)).Providers);
            var stage = await fixture.Host.StageOwnerReleaseAsync("second", new(fixture.Host.RuntimeInstanceId, secondToken, 1, "release"));
            Assert.Equal(permit.AppActivationId, Assert.Single(stage.Retirements).AppActivationId);
            Assert.False(worker.GenerationCompletion.IsCompleted);
            await Assert.ThrowsAsync<RuntimeAuthenticationException>(() => fixture.Host.CommitPackageLifecycleStageAsync(stage.StageId!));
            var retirement = new RuntimeAppActivationRetirementRequest(session, permit.AppActivationId!.Value, permit.PermitId!);
            Assert.Equal("retired", (await policies.RetireActivationAsync(retirement, new(123, null), default)).State);
            var result = await fixture.Host.CommitOwnerReleaseAsync("second", new(fixture.Host.RuntimeInstanceId, secondToken, stage.StageId!));
            Assert.Empty(result.Snapshot.ActivePackages);
            Assert.True(worker.GenerationCompletion.IsCompleted);
            Assert.False((await policies.HeartbeatAsync(session, new(123, null), default)).ShutdownRequired);
            Assert.Single((await policies.GetStatusAsync(session.SessionId, new(123, null), default)).LoadedPackages);
            Assert.Equal(0, process.Terminations);
            Assert.Equal(result, await fixture.Host.CommitOwnerReleaseAsync("second", new(fixture.Host.RuntimeInstanceId, secondToken, stage.StageId!)));
            await Assert.ThrowsAsync<RuntimeAuthenticationException>(() => fixture.Host.CommitOwnerReleaseAsync("second", new(fixture.Host.RuntimeInstanceId, token, stage.StageId!)));
            var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Host.RpcBroker.DiscoverAppAsync(caller.SessionId, "example.rpc", default).AsTask());
            Assert.Equal(SunderRpcErrorKind.Unavailable, unavailable.Error.Kind);
            Assert.Equal("rpc.app-session.unavailable", unavailable.Error.Code);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task ReleaseStageRejectsSameGenerationCoownerChangeInsteadOfOverwritingItsLease()
    {
        var fixture = CreateFixture();
        var folder = CreatePackageLayout(fixture.Paths.RootPath, "race", "test.package", "1.0.0").InstallPath;
        const string token = "retirement-owner-source-revision-token";
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync("first", Mutation(fixture.Host.RuntimeInstanceId, token, "load", 1, folder, false));
            var generation = fixture.Host.SessionGeneration;
            var stage = await fixture.Host.StageOwnerReleaseAsync("first", new(fixture.Host.RuntimeInstanceId, token, 1, "release"));
            await fixture.Host.ReplaceDevPackageOwnerAsync("second", Mutation(fixture.Host.RuntimeInstanceId, token, "load", 1, folder, false));
            Assert.Equal(generation, fixture.Host.SessionGeneration);
            await Assert.ThrowsAsync<RuntimeConflictException>(() => fixture.Host.CommitOwnerReleaseAsync("first", new(fixture.Host.RuntimeInstanceId, token, stage.StageId!)));
            Assert.True(fixture.Host.ContainsDevPackageOwner("first"));
            Assert.True(fixture.Host.ContainsDevPackageOwner("second"));
            Assert.Single(fixture.Host.GetActivePackages());
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task NewAppPermitAfterReleasePreparationBlocksCommitUntilFreshRetirement()
    {
        using var policy = new ActivePolicyTestContext();
        var fixture = CreateFixture(appPolicyFactory: (state, protocol, snapshots, readiness) => new(policy.Paths, protocol, snapshots,
            new(policy.Cache), policy.Cache, policy.Clock, policy.Time, new RetirementProcess(), NullLogger<AppProcessPolicySessions>.Instance, state, readiness));
        var folder = CreateRequiredAppPackage(fixture.Paths.RootPath, web: true);
        const string token = "retirement-new-permit-race-owner-token";
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync("owner", Mutation(fixture.Host.RuntimeInstanceId, token, "load", 1, folder, false));
            var stage = await fixture.Host.StageOwnerReleaseAsync("owner", new(fixture.Host.RuntimeInstanceId, token, 1, "release"));
            Assert.Empty(stage.Retirements);
            var (session, permit, _) = await OpenTrackedAppAsync(fixture.Host);
            await Assert.ThrowsAsync<RuntimeRequiredProviderException>(() => fixture.Host.CommitOwnerReleaseAsync("owner", new(fixture.Host.RuntimeInstanceId, token, stage.StageId!)));
            Assert.True(fixture.Host.ContainsDevPackageOwner("owner"));
            Assert.False((await fixture.Host.AppPolicies!.HeartbeatAsync(session, new(123, null), default)).ShutdownRequired);
            var next = await fixture.Host.StageOwnerReleaseAsync("owner", new(fixture.Host.RuntimeInstanceId, token, 1, "release-again"));
            await fixture.Host.AppPolicies.RetireActivationAsync(new(session, permit.AppActivationId!.Value, permit.PermitId!), new(123, null), default);
            Assert.Empty((await fixture.Host.CommitOwnerReleaseAsync("owner", new(fixture.Host.RuntimeInstanceId, token, next.StageId!))).Snapshot.ActivePackages);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task UnacknowledgedOwnerExpiryFencesAndRetiresTheAffectedAppWithoutTerminatingItsProcess()
    {
        using var policy = new ActivePolicyTestContext();
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var process = new RetirementProcess();
        var fixture = CreateFixture(timeProvider: time, appPolicyFactory: (state, protocol, snapshots, readiness) => new(policy.Paths, protocol, snapshots,
            new(policy.Cache), policy.Cache, policy.Clock, policy.Time, process, NullLogger<AppProcessPolicySessions>.Instance, state, readiness));
        var folder = CreateRequiredAppPackage(fixture.Paths.RootPath, web: true);
        const string token = "retirement-expired-owner-lease-token";
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            var owner = await fixture.Host.ReplaceDevPackageOwnerAsync("owner", Mutation(fixture.Host.RuntimeInstanceId, token, "load", 1, folder, false));
            var (session, permit, _) = await OpenTrackedAppAsync(fixture.Host);
            time.Advance(owner.ExpiresAtUtc - time.GetUtcNow() + TimeSpan.FromSeconds(1));
            await fixture.Host.ReapDevPackageOwnersAsync();
            Assert.Empty(fixture.Host.GetActivePackages());
            // Production runs policy enforcement independently of heartbeat.
            // Advance that lifecycle step explicitly in this unstarted fixture.
            await fixture.Host.AppPolicies!.EnforceAsync(default);
            var status = await fixture.Host.AppPolicies!.HeartbeatAsync(session, new(123, null), default);
            Assert.False(status.ShutdownRequired);
            Assert.Equal(permit.AppActivationId, Assert.Single(status.Retirements).AppActivationId);
            policy.Clock.Advance(TimeSpan.FromSeconds(10));
            await fixture.Host.AppPolicies.EnforceAsync(default);
            Assert.Equal(0, process.Terminations);
            Assert.Equal("retired", (await fixture.Host.AppPolicies.RetireActivationAsync(new(session, permit.AppActivationId!.Value, permit.PermitId!), new(123, null), default)).State);
            Assert.Empty((await fixture.Host.AppPolicies.HeartbeatAsync(session, new(123, null), default)).Retirements);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WatchedAndManualReloadAwaitExactAppRetirementBeforeWithdrawingOldProvider(bool web, bool watched)
    {
        using var policy = new ActivePolicyTestContext();
        var process = new RetirementProcess();
        var fixture = CreateFixture(appPolicyFactory: (state, protocol, snapshots, readiness) => new(policy.Paths, protocol, snapshots,
            new(policy.Cache), policy.Cache, policy.Clock, policy.Time, process, NullLogger<AppProcessPolicySessions>.Instance, state, readiness));
        var folder = CreateRequiredAppPackage(fixture.Paths.RootPath, web);
        const string token = "retirement-watch-reload-owner-token";
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync("owner", Mutation(fixture.Host.RuntimeInstanceId, token, "load", 1, folder, false));
            var (session, permit, caller) = await OpenTrackedAppAsync(fixture.Host);
            var worker = fixture.Host.ActiveSession.LoadedPackageMap["startup.counted.package"].Worker;
            var generation = fixture.Host.SessionGeneration;
            var path = Path.Combine(folder, SunderPackageFormat.ManifestPath);
            var manifest = JsonNode.Parse(File.ReadAllText(path))!;
            manifest["provides"] = new JsonArray();
            var appUse = manifest["usesContracts"]!.AsArray().Single(value => value!["role"]!.GetValue<string>() == "app");
            manifest["usesContracts"]!.AsArray().Remove(appUse);
            File.WriteAllText(path, manifest.ToJsonString()); CanonicalPackageTestBuilder.WriteContentIndex(folder);
            var stage = watched ? await fixture.Host.StageWatchedPackageLifecycleAsync(new(["startup.counted.package"]))
                : await fixture.Host.StagePackageLifecycleAsync(new(["startup.counted.package"]));
            Assert.Equal(permit.AppActivationId, Assert.Single(stage.Retirements).AppActivationId);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var commit = fixture.Host.CommitPackageLifecycleStageAsync(stage.StageId!, deadline.Token);
            try
            {
                var status = await fixture.Host.AppPolicies!.HeartbeatAsync(session, new(123, null), default);
                Assert.Equal(stage.StageId, Assert.Single(status.Retirements).StageId);
                Assert.False(status.ShutdownRequired);
                Assert.False(commit.IsCompleted);
                Assert.Equal(generation, fixture.Host.SessionGeneration);
                Assert.False(worker.GenerationCompletion.IsCompleted);
                Assert.Single((await fixture.Host.RpcBroker.DiscoverAppAsync(caller.SessionId, "example.rpc", default)).Providers);
                await fixture.Host.AppPolicies.RetireActivationAsync(new(session, permit.AppActivationId!.Value, permit.PermitId!), new(123, null), default);
                Assert.True((await commit).Success);
                Assert.True(worker.GenerationCompletion.IsCompleted);
                Assert.False((await fixture.Host.AppPolicies.HeartbeatAsync(session, new(123, null), default)).ShutdownRequired);
                Assert.Empty((await fixture.Host.AppPolicies.GetStatusAsync(session.SessionId, new(123, null), default)).Retirements);
                var (_, fresh, _) = await OpenTrackedAppAsync(fixture.Host, session);
                Assert.NotEqual(permit.PermitId, fresh.PermitId);
                Assert.Equal(0, process.Terminations);
            }
            finally { deadline.Cancel(); try { await commit; } catch (OperationCanceledException) { } }
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task AcknowledgedDevelopmentWithdrawalRestoresInstalledFallbackWithFreshInstalledAdmission()
    {
        using var policy = new ActivePolicyTestContext();
        var fixture = CreateFixture(appPolicyFactory: (state, protocol, snapshots, readiness) => new(policy.Paths, protocol, snapshots,
            new(policy.Cache), policy.Cache, policy.Clock, policy.Time, new RetirementProcess(), NullLogger<AppProcessPolicySessions>.Instance, state, readiness));
        const string token = "retirement-installed-fallback-owner-token";
        var installed = CreatePackageLayout(fixture.Paths.RootPath, "installed", "startup.counted.package", "0.9.0");
        var folder = CreateRequiredAppPackage(fixture.Paths.RootPath, web: false);
        try
        {
            await AddInstalledPackageAsync(fixture.Store, installed);
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync("owner", Mutation(fixture.Host.RuntimeInstanceId, token, "load", 1, folder, false));
            var (session, permit, _) = await OpenTrackedAppAsync(fixture.Host);
            var stage = await fixture.Host.StageOwnerReleaseAsync("owner", new(fixture.Host.RuntimeInstanceId, token, 1, "release"));
            Assert.Single(stage.Retirements);
            await fixture.Host.AppPolicies!.RetireActivationAsync(new(session, permit.AppActivationId!.Value, permit.PermitId!), new(123, null), default);
            var result = await fixture.Host.CommitOwnerReleaseAsync("owner", new(fixture.Host.RuntimeInstanceId, token, stage.StageId!));
            Assert.Equal("0.9.0", Assert.Single(result.Snapshot.ActivePackages).Version);
            var snapshot = Assert.Single(fixture.Host.GetActivePackageUiSnapshots());
            await policy.RefreshAsync(packages: [new(snapshot.PolicySubject!.PackageId, snapshot.PolicySubject.Version, snapshot.PolicySubject.PackageContentIdentity)]);
            var (_, fallback, _) = await OpenTrackedAppAsync(fixture.Host, session);
            Assert.False(fallback.Development);
            Assert.NotEqual(permit.AppActivationId, fallback.AppActivationId);
            Assert.False((await fixture.Host.AppPolicies.HeartbeatAsync(session, new(123, null), default)).ShutdownRequired);
            Assert.Equal(2, (await fixture.Host.AppPolicies.GetStatusAsync(session.SessionId, new(123, null), default)).LoadedPackages.Count);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task ExpiredReleaseStageCannotWithdrawARenewedOwnerAndItsCandidateIsReaped()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var fixture = CreateFixture(policy: new() { PendingStageLifetime = TimeSpan.FromSeconds(1) }, timeProvider: time);
        const string token = "retirement-stage-expiration-owner-token";
        var folder = CreatePackageLayout(fixture.Paths.RootPath, "ttl", "test.package", "1.0.0").InstallPath;
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync("owner", Mutation(fixture.Host.RuntimeInstanceId, token, "load", 1, folder, false));
            var stage = await fixture.Host.StageOwnerReleaseAsync("owner", new(fixture.Host.RuntimeInstanceId, token, 1, "release"));
            time.Advance(TimeSpan.FromSeconds(2));
            await fixture.Host.HeartbeatDevPackageOwnerAsync("owner", new(fixture.Host.RuntimeInstanceId, token));
            await Assert.ThrowsAsync<RuntimeConflictException>(() => fixture.Host.CommitOwnerReleaseAsync("owner", new(fixture.Host.RuntimeInstanceId, token, stage.StageId!)));
            await fixture.Host.SweepLifecycleStagesAsync(time.GetUtcNow());
            Assert.Null(fixture.Host.GetStagedLifecycleSession(stage.StageId!));
            Assert.True(fixture.Host.ContainsDevPackageOwner("owner"));
            Assert.Single(fixture.Host.GetActivePackages());
        }
        finally { await fixture.DisposeAsync(); }
    }

    private static string CreateRequiredAppPackage(string root, bool web)
    {
        var folder = CreateRolePackage(root, "startup.counted.package");
        var path = Path.Combine(folder, SunderPackageFormat.ManifestPath);
        var manifest = JsonNode.Parse(File.ReadAllText(path))!;
        var appUse = manifest["usesContracts"]![0]!.DeepClone();
        appUse["role"] = "app"; appUse["required"] = true;
        manifest["usesContracts"]!.AsArray().Add(appUse);
        if (web)
        {
            var app = manifest["targets"]!.AsArray().Single(value => value!["role"]!.GetValue<string>() == "app")!;
            app["kind"] = "web"; app["entryPoint"] = "index.html"; app["targetFramework"] = null;
            app["views"] = JsonNode.Parse("""[{"viewId":"startup.counted.package.main","displayName":"Main","route":"/","defaultPlacement":"middle","showInHotbar":true}]""");
            File.WriteAllText(Path.Combine(folder, "payload/shared/index.html"), "<main>Web fixture</main>");
        }
        File.WriteAllText(path, manifest.ToJsonString());
        CanonicalPackageTestBuilder.WriteContentIndex(folder);
        return folder;
    }

    private static async Task<(RuntimeAppPolicySessionRequest Session, RuntimeAppPackageLoadPermitResult Permit, RuntimeRpcAppSessionDescriptor Caller)> OpenTrackedAppAsync(RuntimePackageSessionTestHost host, RuntimeAppPolicySessionRequest? existingSession = null)
    {
        var policies = host.AppPolicies!;
        var identity = new RuntimeAppProcessIdentity(123, "birth", new('a', 64), "test", Guid.NewGuid(), host.RuntimeInstanceId);
        var session = existingSession;
        if (session is null)
        {
            var opened = await policies.OpenAsync(new(identity), new(123, null), default);
            session = new(opened.SessionId, identity.AppGenerationId, host.RuntimeInstanceId);
        }
        var snapshot = Assert.Single(host.GetActivePackageUiSnapshots());
        var source = host.GetActiveRuntimePackageSources().Single();
        var target = PackageTargetSelection.ToDescriptor(source.Manifest!.Targets!.Single(value => value!.Role == "app")!);
        var activation = Guid.NewGuid();
        var permit = await policies.PermitAsync(new(session, snapshot.PolicySubject!) { SnapshotId = snapshot.SnapshotId, AppActivationId = activation, AppTarget = target }, new(123, null), default);
        var caller = await host.AppRpcSessions!.OpenAsync(new(source.PackageId, source.Manifest.Version!, RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex!), target, host.SessionGeneration, activation)
            { LoadPermitId = permit.PermitId }, default, new(123, null));
        return (session, permit, caller);
    }

    private sealed class RetirementProcess : IAppPolicyProcessController, IAppPolicyProcessHandle
    {
        internal int Terminations;
        public IAppPolicyProcessHandle Attach(RuntimeAppProcessIdentity identity, AppPolicyPeer peer, bool recovery) => this;
        public AppPolicyProcessState Probe() => AppPolicyProcessState.Alive;
        public void Terminate() => Terminations++;
        public void Dispose() { }
    }
}
