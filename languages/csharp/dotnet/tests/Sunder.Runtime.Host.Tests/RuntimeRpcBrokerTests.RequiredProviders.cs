using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public void RetainedPackagePrivateAppObligationRejectsAnExactForeignPublicProvider()
    {
        using var permissions = new RuntimeRpcPermissionStore(new(CreateRoot()));
        var readiness = new RuntimeRpcReadinessEvaluator(permissions); var descriptor = Contract();
        var source = new RuntimePackageSource("app.package", PackageSourceKind.Dev, CreateRoot(), Manifest: new()
        {
            Id = "app.package", Version = "1.0.0",
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
    }
}
