using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Sdk.Settings;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RuntimeRpcBrokerTests
{
    [Fact]
    public async Task DeclaredButUnacquiredAppTargetCannotCreateRpcOrProjectionAuthority()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler(), webCaller: true,
            transform: package => package.Descriptor.PackageId == "caller.package"
                ? package with { Source = package.Source with { AcquiredTargets = [] } } : package);
        var source = activation.Session.LoadedPackageMap["caller.package"].Source;
        var target = Assert.Single(source.Manifest!.Targets!)!;
        Assert.NotEmpty(source.Manifest.UsesContracts!);
        await Assert.ThrowsAsync<RuntimeValidationException>(() => fixture.OpenAppCallerAsync());
        Assert.Empty(RuntimeRpcPermissionSubjects.FromSession(activation.Session).Single(subject => subject.PackageId == "caller.package").ContractUses);
        Assert.Throws<InvalidDataException>(() => PackageProjectionRequestFactory.CreateApp(new(source, new(target.Role!, target.Rid!), target)));
    }

    [Fact]
    public void CandidateEndpointsAppearOnlyAfterAdvertisementAndReadyAcknowledgement()
    {
        using var catalog = new RuntimeRpcCatalog();
        var consumer = CreatePackage("consumer.package", Contract(), null, true, PackageSourceKind.Dev);
        var provider = CreatePackage("provider.package", Contract(), new TestHandler(), false);
        var session = CreateSession(consumer, provider);
        catalog.PrepareStartingCandidate(session, 1);
        Assert.True(catalog.TryGetPackage(consumer.Descriptor.PackageId, consumer.RuntimeActivationId, out var caller));
        Assert.True(catalog.TryGetPackage(provider.Descriptor.PackageId, provider.RuntimeActivationId, out var self));
        Assert.Empty(catalog.GetSnapshot(caller!, _ => true).Providers);
        catalog.MarkCandidateProviderReady(session, provider.Descriptor.PackageId, ready: false);
        Assert.Single(catalog.GetSnapshot(self!, _ => true).Providers);
        Assert.Empty(catalog.GetSnapshot(caller!, _ => true).Providers);
        catalog.MarkCandidateProviderReady(session, provider.Descriptor.PackageId, ready: true);
        var endpoint = Assert.Single(catalog.GetSnapshot(caller!, _ => true).Providers).Endpoint;
        Assert.Empty(catalog.GetSnapshot().Providers);
        catalog.MarkCandidateProviderReady(session, consumer.Descriptor.PackageId, ready: true);
        catalog.CompleteStartingCandidate(session);
        catalog.ActivateSession(session, 1);
        Assert.Equal(endpoint, Assert.Single(catalog.GetSnapshot().Providers).Endpoint);
    }

    [Fact]
    public void RequiredProviderPlanUsesExactDescriptorsAndAuthorizedAlternatives()
    {
        var root = CreateRoot();
        using var permissions = new RuntimeRpcPermissionStore(new(root));
        var readiness = new RuntimeRpcReadinessEvaluator(permissions);
        var descriptor = Contract();
        var consumer = CreatePackage("consumer.package", descriptor, null, true, PackageSourceKind.Dev);
        var first = CreatePackage("first.package", descriptor, new TestHandler(), false, providerId: "first.provider");
        var second = CreatePackage("second.package", descriptor, new TestHandler(), false, providerId: "second.provider");
        var plan = readiness.Plan(CreateSession(consumer, first, second));
        var requirement = Assert.Single(plan.Predicates);
        Assert.Equal(descriptor.Sha256, requirement.Details.Contract.DescriptorSha256);
        Assert.Equal(["first.package", "second.package"], requirement.Alternatives);
        Assert.False(plan.CanStart("consumer.package", new HashSet<string>()));
        Assert.True(plan.CanStart("consumer.package", new HashSet<string> { "second.package" }));
    }

    [Fact]
    public void RequiredProviderPlanRejectsMissingOrPrivateForeignProvidersBeforeStartup()
    {
        using var permissions = new RuntimeRpcPermissionStore(new(CreateRoot()));
        var readiness = new RuntimeRpcReadinessEvaluator(permissions);
        var consumer = CreatePackage("consumer.package", Contract(), null, true, PackageSourceKind.Dev);
        var foreign = CreatePackage("foreign.package", Contract(), new TestHandler(), false, providerAudience: "owner");
        var missing = Assert.Throws<RuntimeRequiredProviderException>(() => readiness.Plan(CreateSession(consumer, foreign)));
        Assert.Equal(RpcReadinessErrorCodes.RequiredProviderUnsatisfied, missing.Code);
        Assert.Equal(["consumer.package"], missing.PackageIds);
    }

    [Fact]
    public void RequiredProviderOrCycleIsBrokenByAnIndependentAlternative()
    {
        var contract = new RuntimeRpcContractIdentity("example.rpc", "1.0.0", new('a', 64), new('b', 64));
        Dictionary<string, IReadOnlyList<string>> dependencies = new(StringComparer.Ordinal) { ["a"] = [], ["b"] = [], ["c"] = [] };
        var cycle = new RuntimeRequiredProviderPlan(dependencies,
            [new(new("a", "runtime", contract, ["invoke"]), ["b"]), new(new("b", "runtime", contract, ["invoke"]), ["a"])]);
        Assert.Equal(RpcReadinessErrorCodes.RequiredProviderCycle, Assert.Throws<RuntimeRequiredProviderException>(cycle.Validate).Code);
        var alternative = new RuntimeRequiredProviderPlan(dependencies,
            [new(new("a", "runtime", contract, ["invoke"]), ["b", "c"]), new(new("b", "runtime", contract, ["invoke"]), ["a"])]);
        alternative.Validate();
    }

    [Fact]
    public async Task RequiredProviderSchedulerStartsConsumerWithoutWaitingForUnnecessarySlowAlternative()
    {
        using var permissions = new RuntimeRpcPermissionStore(new(CreateRoot()));
        var readiness = new RuntimeRpcReadinessEvaluator(permissions);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var descriptor = Contract();
        var consumer = CreatePackage("consumer.package", descriptor, null, true, PackageSourceKind.Dev) with
        { Worker = new RequiredStartWorker(_ => { consumerStarted.TrySetResult(); return Task.CompletedTask; }) };
        var fast = CreatePackage("fast.package", descriptor, new TestHandler(), false, providerId: "fast.provider");
        var slow = CreatePackage("slow.package", descriptor, new TestHandler(), false, providerId: "slow.provider") with
        { Worker = new RequiredStartWorker(token => releaseSlow.Task.WaitAsync(token)) };
        var prepared = CreateSession(consumer, fast, slow);
        var session = new ActivePackageSession(null, prepared.LoadedPackageMap.ToDictionary(pair => pair.Key, pair => pair.Value),
            prepared.GetSessionPackages().ToDictionary(value => value.PackageId), workersStarted: false, readySources: prepared.GetActivePackageSources(), maximumWorkerStartupConcurrency: 2);
        session.RequiredProviderPlan = readiness.Plan(session);
        var startup = session.StartWorkersAsync(NullLogger.Instance, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2));
        try
        {
            await consumerStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(startup.IsCompleted);
        }
        finally { releaseSlow.TrySetResult(); await startup; await session.DisposeAsync(); }
    }

    [Fact]
    public async Task SameWorkerRequiredProviderIsAdvertisedBeforeItsStartCallback()
    {
        using var permissions = new RuntimeRpcPermissionStore(new(CreateRoot()));
        var readiness = new RuntimeRpcReadinessEvaluator(permissions);
        var composed = false;
        var package = CreatePackage("self.package", Contract(), new TestHandler(), true, PackageSourceKind.Dev, providerAudience: "owner") with
        { Worker = new RequiredStartWorker(_ => { Assert.True(composed); return Task.CompletedTask; }) };
        var prepared = CreateSession(package);
        var session = new ActivePackageSession(null, prepared.LoadedPackageMap.ToDictionary(pair => pair.Key, pair => pair.Value),
            prepared.GetSessionPackages().ToDictionary(value => value.PackageId), workersStarted: false, readySources: prepared.GetActivePackageSources());
        session.RequiredProviderPlan = readiness.Plan(session);
        session.CandidateProviderReady = (_, ready) => { if (!ready) composed = true; };
        await session.StartWorkersAsync(NullLogger.Instance, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        await session.DisposeAsync();
    }

    private sealed class RequiredStartWorker(Func<CancellationToken, Task> start) : TestRuntimePackageWorker
    { public override Task StartAsync(CancellationToken cancellationToken = default) => start(cancellationToken); }

    [Fact]
    public async Task LastRequiredProviderWithdrawalStopsConsumerButAnAlternativeKeepsItActive()
    {
        using var permissions = new RuntimeRpcPermissionStore(new(CreateRoot()));
        var readiness = new RuntimeRpcReadinessEvaluator(permissions);
        using var catalog = new RuntimeRpcCatalog();
        var owner = new RuntimeSessionOwner(NullLogger<RuntimeSessionOwner>.Instance, new RuntimeEventStreamService(), rpcCatalog: catalog, rpcReadiness: readiness);
        var descriptor = Contract();
        var binding = TestExecutionRuntimeLaunchResolver.CreateBinding("runtime", "osx-arm64");
        var consumer = CreatePackage("consumer.package", descriptor, null, true, PackageSourceKind.Dev) with { ExecutionRuntimeBinding = binding };
        var first = CreatePackage("first.package", descriptor, new TestHandler(), false, providerId: "first.provider") with { ExecutionRuntimeBinding = binding };
        var second = CreatePackage("second.package", descriptor, new TestHandler(), false, providerId: "second.provider") with { ExecutionRuntimeBinding = binding };
        var session = CreateSession(consumer, first, second);
        session.RequiredProviderPlan = readiness.Plan(session);
        try
        {
            await owner.PublishAsync(session, owner.Sources.Snapshot(), [], [], owner.Generation);
            await session.CommitRuntimeGenerationAsync(owner.Generation);
            catalog.ActivateSession(session, owner.Generation);
            Assert.True(owner.FaultPackageExecution("first.package", "test withdrawal", out var firstCleanup));
            await firstCleanup;
            Assert.Equal(PackageReadinessState.Ready, owner.State.GetSessionPackage("consumer.package")!.Readiness);
            Assert.True(owner.FaultPackageExecution("second.package", "test withdrawal", out var secondCleanup));
            await secondCleanup;
            Assert.Equal(PackageReadinessState.Failed, owner.State.GetSessionPackage("consumer.package")!.Readiness);
            Assert.Equal(RpcReadinessErrorCodes.RequiredProviderUnsatisfied, owner.State.GetSessionPackage("consumer.package")!.LastError);
        }
        finally { await owner.State.ClearActiveSessionAsync(); }
    }

    [Fact]
    public async Task RejectedPublicationDefersOnlyConsumersWhoseLastRequiredProviderWasLost()
    {
        using var permissions = new RuntimeRpcPermissionStore(new(CreateRoot()));
        var readiness = new RuntimeRpcReadinessEvaluator(permissions);
        using var catalog = new RuntimeRpcCatalog();
        var owner = new RuntimeSessionOwner(NullLogger<RuntimeSessionOwner>.Instance, new RuntimeEventStreamService(), rpcCatalog: catalog, rpcReadiness: readiness);
        var publisher = new PackageSessionPublisher(owner, NullLogger<PackageSessionPublisher>.Instance, rpcCatalog: catalog, rpcReadiness: readiness);
        var provider = CreatePackage("provider.package", Contract(), new TestHandler(), false);
        var consumer = CreatePackage("consumer.package", Contract(), null, true, PackageSourceKind.Dev);
        var healthy = CreatePackage("healthy.package", Contract(), null, false);
        var previous = Unstarted(provider, consumer, healthy);
        var blocked = new RejectedProviderCandidateWorker();
        try
        {
            await publisher.PublishAsync(previous, owner.Sources.Snapshot(), [], [], 0, default);
            var rejected = publisher.PublishAsync(Unstarted(CreatePackage("candidate.package", Contract(), null, false) with { Worker = blocked }),
                owner.Sources.Snapshot(), [], [], owner.Generation, default);
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            owner.HandleRuntimeGenerationFault(provider.Descriptor.PackageId, new(provider.RuntimeActivationId, 1), new IOException("transport drain failed"));
            blocked.Release.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => rejected);
            Assert.Same(previous, owner.State.ActiveSession);
            Assert.Equal(PackageReadinessState.Failed, owner.State.GetSessionPackage("consumer.package")!.Readiness);
            Assert.Equal(PackageReadinessState.Ready, owner.State.GetSessionPackage("healthy.package")!.Readiness);
            using var lease = owner.State.AcquireLease();
            Assert.Same(previous, lease.Session);
            var retry = new RuntimePackageRecoveryRequest(lease.Identity, previous, "consumer.package", consumer.RuntimeActivationId,
                owner.Generation, consumer.Source, consumer, Task.CompletedTask, PackageFailureOrigin.RuntimeActivation,
                new InvalidOperationException("Required provider unavailable."), DateTimeOffset.UtcNow);
            Assert.Equal(RuntimePackageRecoveryValidation.DependencyUnavailable, owner.State.ValidatePackageRecovery(retry));
        }
        finally { blocked.Release.TrySetResult(); await owner.State.ClearActiveSessionAsync(); }

        static ActivePackageSession Unstarted(params ActiveLoadedPackage[] packages)
        {
            var prepared = CreateSession(packages.Select(package => package with
                { Settings = new PackageSettings(new JsonPackageKeyValueStore(Path.Combine(package.Source.SourceFolder, "settings.json"))) }).ToArray());
            return new(null, prepared.LoadedPackageMap.ToDictionary(pair => pair.Key, pair => pair.Value),
                prepared.GetSessionPackages().ToDictionary(package => package.PackageId), workersStarted: false,
                readySources: prepared.GetActivePackageSources());
        }
    }

    private sealed class RejectedProviderCandidateWorker : TestRuntimePackageWorker
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task CommitGenerationAsync(RuntimePackageGeneration generation, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("candidate rejected");
        }
    }

    [Fact]
    public async Task RecoveryProviderReadinessAllowsOwnAdvertisementButWaitsForMissingForeignProviders()
    {
        using var permissions = new RuntimeRpcPermissionStore(new(CreateRoot()));
        var readiness = new RuntimeRpcReadinessEvaluator(permissions);
        var self = CreatePackage("self.package", Contract(), new TestHandler(), true, PackageSourceKind.Dev, providerAudience: "owner");
        await using var ownSession = CreateSession(self);
        Assert.True(ownSession.MarkPackageFailed("self.package", PackageFailureOrigin.RuntimeProcess, "failed", out _));
        Assert.True(readiness.AreRecoveryProvidersAvailable(ownSession, self));
        var foreign = CreatePackage("foreign.consumer", Contract(), null, true, PackageSourceKind.Dev);
        await using var missingSession = CreateSession(foreign);
        Assert.True(missingSession.MarkPackageFailed("foreign.consumer", PackageFailureOrigin.RuntimeProcess, "failed", out _));
        Assert.False(readiness.AreRecoveryProvidersAvailable(missingSession, foreign));
    }

    [Fact]
    public void AppRequiredPredicatesMatchExactIdentityAndSurviveOneAlternativeWithdrawal()
    {
        using var permissions = new RuntimeRpcPermissionStore(new(CreateRoot()));
        var readiness = new RuntimeRpcReadinessEvaluator(permissions);
        var descriptor = Contract();
        var first = CreatePackage("first.package", descriptor, new TestHandler(), false, providerId: "first.provider");
        var second = CreatePackage("second.package", descriptor, new TestHandler(), false, providerId: "second.provider");
        var predicate = new RuntimeRequiredProviderPredicate("app.package", "app",
            new(descriptor.ContractId, descriptor.Version, descriptor.Sha256, descriptor.AbiSha256), ["invoke"]);
        Assert.True(readiness.AppPredicatesSatisfied(CreateSession(first, second), "1.0.0", true, null, [predicate]));
        Assert.True(readiness.AppPredicatesSatisfied(CreateSession(second), "1.0.0", true, null, [predicate]));
        Assert.False(readiness.AppPredicatesSatisfied(CreateSession(), "1.0.0", true, null, [predicate]));
        Assert.False(readiness.AppPredicatesSatisfied(CreateSession(second), "1.0.0", true, null,
            [predicate with { Contract = predicate.Contract with { DescriptorSha256 = new('f', 64) } }]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnplannedProviderLossIsPackageLocalAndTrackedRetirementSurvivesProviderRecovery(bool tracked, bool web)
    {
        using var permissions = new RuntimeRpcPermissionStore(new(CreateRoot()));
        var readiness = new RuntimeRpcReadinessEvaluator(permissions); var descriptor = Contract();
        var target = new SunderPackageTargetManifest { Role = "app", Rid = "osx-arm64", Kind = web ? "web" : "avalonia",
            EntryPoint = web ? "index.html" : "lib/App.dll", TargetFramework = web ? null : "net10.0", SdkVersion = "1.1.0", RequiredHostCapabilities = [] };
        var source = new RuntimePackageSource("app.package", PackageSourceKind.Dev, CreateRoot(), ContentIdentity: new('a', 64), Manifest: new()
        {
            Id = "app.package", Version = "1.0.0",
            Targets = [target],
            ContractBundles = [new() { ContractId = descriptor.ContractId, Version = descriptor.Version, DescriptorSha256 = descriptor.Sha256,
                AbiSha256 = descriptor.AbiSha256, Exposure = SunderPackageFormat.PackageContractExposure }],
            UsesContracts = [new() { Role = "app", ContractId = descriptor.ContractId, ContractVersion = descriptor.Version,
                AbiSha256 = descriptor.AbiSha256, Required = true, Actions = ["invoke"] }],
        });
        var predicates = RuntimeRpcReadinessEvaluator.AppPredicates(source);
        var ownerOnly = RuntimeRpcReadinessEvaluator.AppOwnerOnlyContracts(source);
        var foreign = CreatePackage("foreign.package", descriptor, new TestHandler(), false);
        Assert.False(readiness.AppPredicatesSatisfied(CreateSession(foreign), "1.0.0", true, null, predicates, ownerOnly));
        var owner = CreatePackage("app.package", descriptor, new TestHandler(), false, providerAudience: "owner");
        Assert.True(readiness.AppPredicatesSatisfied(CreateSession(owner, foreign), "1.0.0", true, null, predicates, ownerOnly));
        Assert.False(readiness.AppPredicatesSatisfied(CreateSession(foreign), "1.0.0", true, null, predicates, ownerOnly));

        using var policy = new ActivePolicyTestContext();
        var runtime = new PackageSessionState(NullLogger.Instance, () => { }, _ => { });
        var snapshots = new RequiredProviderSnapshots(source);
        var process = new RequiredProviderProcess();
        var logs = new RecordingLogSink();
        var protocol = new RuntimeProtocolDescriptor();
        using var sessions = new AppProcessPolicySessions(policy.Paths, protocol, snapshots, new(policy.Cache), policy.Cache,
            policy.Clock, policy.Time, process, logs.CreateLogger<AppProcessPolicySessions>(), runtime, readiness);
        var peer = new AppPolicyPeer(123, null);
        var identity = new RuntimeAppProcessIdentity(peer.ProcessId, "birth", new('b', 64), "test", Guid.NewGuid(), protocol.RuntimeInstanceId);
        try
        {
            await runtime.PublishSessionAsync(CreateSession(owner, foreign));
            var opened = await sessions.OpenAsync(new(identity), peer, default);
            var request = new RuntimeAppPolicySessionRequest(opened.SessionId, identity.AppGenerationId, protocol.RuntimeInstanceId);
            var load = new RuntimeAppPackageLoadPermitRequest(request, new(source.PackageId, "1.0.0", source.ContentIdentity!))
            {
                SnapshotId = new('a', 32), AppActivationId = tracked ? Guid.NewGuid() : null,
                AppTarget = tracked ? PackageTargetSelection.ToDescriptor(target) : null,
            };
            var permit = await sessions.PermitAsync(load, peer, default);
            Assert.True(permit.Allowed);
            var authority = tracked ? sessions.RequireCallerPermit(peer, source, load.AppActivationId!.Value,
                permit.PermitId, load.AppTarget!) : null;
            using var inFlight = authority?.Acquire();
            snapshots.Source = null;
            Assert.False((await sessions.HeartbeatAsync(request, peer, default)).ShutdownRequired);

            // Ordinary provider failure must never become OS-process termination.
            // Tracked activations retire exactly; legacy callers fail availability
            // checks without blocking unrelated packages or losing security subjects.
            await runtime.PublishSessionAsync(CreateSession(foreign));
            // Availability retirement belongs to lifecycle enforcement, not the
            // authenticated heartbeat's independent security-proof path.
            await sessions.EnforceAsync(default);
            var status = await sessions.HeartbeatAsync(request, peer, default);
            Assert.False(status.ShutdownRequired);
            Assert.Equal(RuntimeAppPolicySessionStates.Active, status.State);
            Assert.Throws<RuntimeUnavailableException>(() => sessions.RequireLoaded(peer, source));
            if (tracked)
            {
                var retirement = Assert.Single(status.Retirements);
                Assert.Equal(load.AppActivationId, retirement.AppActivationId);
                Assert.Equal(permit.PermitId, retirement.LoadPermitId);
                Assert.False(authority!.IsActive);
                Assert.True(authority.RetirementToken.IsCancellationRequested);
                using var cancelledCleanup = new CancellationTokenSource();
                var retiring = sessions.RetireActivationAsync(new(request, retirement.AppActivationId, retirement.LoadPermitId), peer, cancelledCleanup.Token);
                Assert.False(retiring.IsCompleted);
                Assert.False((await sessions.HeartbeatAsync(request, peer, default)).ShutdownRequired);
                cancelledCleanup.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retiring);
                Assert.Single((await sessions.HeartbeatAsync(request, peer, default)).Retirements);
                inFlight!.Dispose();
            }
            else Assert.Empty(status.Retirements);
            Assert.Equal(source.PackageId, Assert.Single(status.LoadedPackages).PackageId);
            var persistedPath = Directory.EnumerateFiles(Path.Combine(policy.Paths.RootPath,
                Sunder.Runtime.LocalState.RuntimeV1StateDescriptor.AppPolicySessionsDirectory), "*.json").Single();
            var record = JsonSerializer.Deserialize<AppPolicySessionFile>(File.ReadAllText(persistedPath),
                new JsonSerializerOptions(RegistryActivePolicyClient.Json) { IncludeFields = true })!;
            Assert.Null(record.Cause);
            var retained = Assert.Single(Assert.Single(record.Loaded).RequiredProviders);
            Assert.Equal(predicates[0].ConsumerPackageId, retained.ConsumerPackageId);
            Assert.Equal(predicates[0].ConsumerRole, retained.ConsumerRole);
            Assert.Equal(predicates[0].Contract, retained.Contract);
            Assert.Equal(predicates[0].Actions, retained.Actions);
            Assert.Equal(ownerOnly, Assert.Single(record.Loaded).OwnerOnlyContracts);
            var unrelated = source with { PackageId = "unrelated.package", ContentIdentity = new('e', 64),
                Manifest = new() { Id = "unrelated.package", Version = "1.0.0" } };
            snapshots.Source = unrelated;
            Assert.True((await sessions.PermitAsync(new(request, new(unrelated.PackageId, "1.0.0", unrelated.ContentIdentity!))
                { SnapshotId = new('a', 32) }, peer, default)).Allowed);
            sessions.RequireLoaded(peer, unrelated);
            policy.Clock.Advance(TimeSpan.FromSeconds(10));
            await sessions.EnforceAsync(default);
            Assert.Equal(0, process.Terminations);
            Assert.True(File.Exists(persistedPath));

            await runtime.PublishSessionAsync(CreateSession(owner, foreign));
            snapshots.Source = source;
            if (tracked)
            {
                // Recovery cannot revive a fenced activation or erase cleanup debt.
                Assert.Single((await sessions.HeartbeatAsync(request, peer, default)).Retirements);
                await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => sessions.PermitAsync(load, peer, default));
                var retired = await sessions.RetireActivationAsync(new(request, load.AppActivationId!.Value, permit.PermitId!), peer, default);
                Assert.Equal("retired", retired.State);
                Assert.Empty((await sessions.HeartbeatAsync(request, peer, default)).Retirements);
                Assert.True((await sessions.PermitAsync(load with { AppActivationId = Guid.NewGuid() }, peer, default)).Allowed);
                Assert.False(authority!.IsActive);
            }
            else sessions.RequireLoaded(peer, source);
            process.State = AppPolicyProcessState.ExitProven;
            await sessions.EnforceAsync(default);
            Assert.False(File.Exists(persistedPath));
        }
        finally { await runtime.ClearActiveSessionAsync(); }
    }

    private sealed class RequiredProviderSnapshots(RuntimePackageSource source) : IAppPolicySnapshotSource
    {
        internal RuntimePackageSource? Source { get; set; } = source;
        public RuntimePackageSource? PolicySource(string snapshotId) => snapshotId == new string('a', 32) ? Source : null;
    }

    private sealed class RequiredProviderProcess : IAppPolicyProcessController, IAppPolicyProcessHandle
    {
        internal AppPolicyProcessState State = AppPolicyProcessState.Alive;
        internal int Terminations;
        public IAppPolicyProcessHandle Attach(RuntimeAppProcessIdentity identity, AppPolicyPeer peer, bool recovery) => this;
        public AppPolicyProcessState Probe() => State;
        public void Terminate() => Terminations++;
        public void Dispose() { }
    }
}
