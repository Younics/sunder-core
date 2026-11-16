using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using System.Reflection;
using Xunit;

namespace Sunder.App.Tests;

public sealed partial class PackageViewHostServiceTests
{
    [Fact]
    public Task RepeatedRetirementFenceDoesNotSupersedeTheNextUnrelatedPresentation()
        => RunPackageTestAsync("app-retirement-fence-once", async temporary =>
    {
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, CreateAppPackageSource(temporary.RootPath, "agent"));
        var package = CreateActiveAgentPackage();
        await using var host = new PackageViewHostService(new(), [], [], [], temporary.GetPath("session"),
            uiDispatcher: TestUiDispatcher, packagePolicy: TestAppPackagePolicy.Instance,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);
        await host.ApplyPackageDeltaAsync([package], [source]);
        var owner = Assert.Single(RetentionGeneration(host).ActivationGroups, group => group.OwnedActivationIds.Contains("agent"));
        var publisher = (AppPackageGenerationPublisher)typeof(PackageViewHostService)
            .GetField("_generationPublisher", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;
        publisher.FenceActivationForRetirement(owner, () => { });
        var basis = CreateRuntimeSnapshot(package, source, 2);
        var empty = new RuntimePackageSnapshot(basis.RuntimeInstanceId, 2, basis.EventSequence, RuntimeBootstrapState.Ready, [], [], [], []);
        await host.ApplyPackageGenerationAsync(empty, [], null, (_, _, _) =>
        {
            publisher.FenceActivationForRetirement(owner, () => { });
            return Task.FromResult<Action?>(null);
        }, default);
        await host.WaitForRetirementsAsync();
        Assert.Equal(0, host.LoadedPackageCount);
    });

    [Theory]
    [Trait("Capability", "Process")]
    [InlineData(false)]
    [InlineData(true)]
    public Task MissingPackageSourceDoesNotBlockHealthyPresentationButReadmissionStillRequiresSettledState(bool uncertainMutation)
        => RunPackageTestAsync("app-missing-source-isolation", async temporary =>
    {
        var agent = CreateActiveAgentPackage();
        var sibling = CreateActivePackage("project") with { Views = [] };
        var agentSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, CreateAppPackageSource(temporary.GetPath("agent"), "agent")) with
            { ActivationFingerprint = new('a', 64) };
        var siblingSource = RuntimeContractTestData.Snapshot("project", PackageSourceKind.Dev, CreateAppPackageSource(temporary.GetPath("project"), "project")) with
            { ActivationFingerprint = new('b', 64) };
        await using var host = new PackageViewHostService(new(), [], [], [], temporary.GetPath("session"), uiDispatcher: TestUiDispatcher,
            packagePolicy: TestAppPackagePolicy.Instance, downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);
        await host.ApplyPackageDeltaAsync([agent, sibling], [agentSource, siblingSource]);
        var healthy = RetentionGeneration(host).State.GetLoadedPackage("project")!;
        var owner = Assert.Single(RetentionGeneration(host).ActivationGroups, group => group.OwnedActivationIds.Contains("agent"));
        if (uncertainMutation)
        {
            var publication = (AppPackageGenerationPublication)typeof(AppPackageHostComposition)
                .GetField("_publication", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner.Composition)!;
            await Assert.ThrowsAsync<IOException>(() => publication.RunDataMutationAsync(
                () => Task.FromException(new IOException("The original write has no settlement acknowledgement."))));
        }
        await host.ApplyPackageDeltaAsync([agent, sibling], [siblingSource]);
        await host.WaitForRetirementsAsync();
        Assert.Equal(1, host.LoadedPackageCount);
        Assert.Same(healthy.ServiceProvider, RetentionGeneration(host).State.GetLoadedPackage("project")!.ServiceProvider);
        if (uncertainMutation)
        {
            Assert.False(owner.Composition.HasAcknowledgedRetirement("agent"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.ApplyPackageDeltaAsync([agent, sibling], [agentSource, siblingSource]));
        }
        else await host.ApplyPackageDeltaAsync([agent, sibling], [agentSource, siblingSource]);
        await host.WaitForRetirementsAsync();
        Assert.Equal(uncertainMutation ? 1 : 2, host.LoadedPackageCount);
        Assert.Same(healthy.ServiceProvider, RetentionGeneration(host).State.GetLoadedPackage("project")!.ServiceProvider);
    }, untilProcessExit: uncertainMutation, arguments: [uncertainMutation]);

    [Fact]
    [Trait("Capability", "Process")]
    public Task ReplacedUnprovenOwnerRemainsAvailableForExactRetirementAfterCollection()
        => RunPackageTestAsync("app-retirement-owner-proof", async temporary =>
    {
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, CreateAppPackageSource(temporary.RootPath, "agent")) with
            { PolicySubject = new("agent", "1.0.0", new('b', 64)) };
        var policy = new ExactRetentionPolicy(source);
        using var handler = new RetentionRpcHandler(policy);
        using var transport = new RuntimeClientTransport(() => new(new Uri("http://runtime.test/"), "test-token"), handler);
        await using var host = new PackageViewHostService(new(), [], [], [], temporary.GetPath("session"),
            getRuntimeConnectionInfo: transport.GetConnectionInfo, uiDispatcher: TestUiDispatcher, packagePolicy: policy,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);
        var (requirement, owner) = await ReplaceUnprovenOwnerAsync(host, source);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(owner.TryGetTarget(out _), "Presentation removal must not erase an unproven retirement owner.");
        await host.RetireRequestedActivationsAsync([requirement], null, default);
        await host.RetireRequestedActivationsAsync([requirement], null, default);
        Assert.Empty(policy.RetiredActivationIds);
        Assert.Equal(0, host.LoadedPackageCount);
    }, untilProcessExit: true);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<(RuntimeAppActivationRetirementRequirement, WeakReference<AppPackageGeneration>)> ReplaceUnprovenOwnerAsync(
        PackageViewHostService host, PackageUiSnapshotDescriptor source)
    {
        await host.ApplyPackageDeltaAsync([CreateActiveAgentPackage()], [source]);
        var group = Assert.Single(RetentionGeneration(host).ActivationGroups, group => group.OwnedActivationIds.Contains("agent"));
        var handle = group.State.GetLoadedPackage("agent")!;
        group.State.SetLoadedPackage("agent", handle with { TargetLifetime = new FailedRetirementLifetime(handle.TargetLifetime!) });
        var admission = handle.LoadAdmission!;
        var requirement = new RuntimeAppActivationRetirementRequirement("test-policy", admission.ActivationId, admission.PermitId!, source.PolicySubject!);
        var owner = new WeakReference<AppPackageGeneration>(group);
        await host.ApplyPackageDeltaAsync([], []);
        await host.WaitForRetirementsAsync();
        return (requirement, owner);
    }

    [Fact]
    [Trait("Capability", "Process")]
    public Task TerminalLocalCleanupFailureIsQuarantinedOnceAndNeverAcknowledgedAsRetired()
        => RunPackageTestAsync("app-terminal-retirement", async temporary =>
    {
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, CreateAppPackageSource(temporary.RootPath, "agent")) with
            { PolicySubject = new("agent", "1.0.0", new('b', 64)) };
        var policy = new ExactRetentionPolicy(source);
        using var handler = new RetentionRpcHandler(policy);
        using var transport = new RuntimeClientTransport(() => new(new Uri("http://runtime.test/"), "test-token"), handler);
        await using var host = new PackageViewHostService(new(), [], [], [], temporary.GetPath("session"),
            getRuntimeConnectionInfo: transport.GetConnectionInfo, uiDispatcher: TestUiDispatcher, packagePolicy: policy,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);
        await host.ApplyPackageDeltaAsync([CreateActiveAgentPackage()], [source]);
        var owner = Assert.Single(RetentionGeneration(host).ActivationGroups, group => group.OwnedActivationIds.Contains("agent"));
        var handle = owner.State.GetLoadedPackage("agent")!;
        var failing = new FailedRetirementLifetime(handle.TargetLifetime!);
        owner.State.SetLoadedPackage("agent", handle with { TargetLifetime = failing });
        var admission = handle.LoadAdmission!;
        var requirement = new RuntimeAppActivationRetirementRequirement("test-policy", admission.ActivationId, admission.PermitId!, source.PolicySubject!)
            { StageId = "terminal-cleanup-stage" };
        await host.RetireRequestedActivationsAsync([requirement], null, default);
        await host.RetireRequestedActivationsAsync([requirement], null, default);
        Assert.Equal(1, failing.Disposals);
        Assert.Empty(policy.RetiredActivationIds);
        Assert.False(owner.Composition.ServicesPublished);
    }, untilProcessExit: true);

    private sealed class FailedRetirementLifetime(IAsyncDisposable inner) : IAsyncDisposable
    {
        internal int Disposals;
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            await inner.DisposeAsync();
            throw new IOException("The original owner release was not acknowledged.");
        }
    }

    [Fact]
    public Task RevokedRetainedActivationSupersedesPreparationInsteadOfAcquiringPendingAuthority()
        => RunPackageTestAsync("app-retained-revocation-preparation", async temporary =>
    {
        var folder = CreateAppPackageSource(temporary.RootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, folder) with
            { ActivationFingerprint = new('a', 64) };
        await using var host = new PackageViewHostService(new(), [], [], [], temporary.GetPath("session"),
            uiDispatcher: TestUiDispatcher, packagePolicy: TestAppPackagePolicy.Instance,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);
        await host.ApplyPackageDeltaAsync([package], [source]);
        var owner = Assert.Single(RetentionGeneration(host).ActivationGroups, group => group.OwnedActivationIds.Contains("agent"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ApplyPackageGenerationAsync(
            CreateRuntimeSnapshot(package, source, 2), [source with { SessionGeneration = 2 }], null,
            async (candidate, _, token) =>
            {
                Assert.Contains("agent", candidate.RetainedPackageIds);
                owner.Composition.UnpublishServices();
                await candidate.StabilizeViewAsync("agent.chat", cancellationToken: token);
                return null;
            }, default));
        await host.WaitForRetirementsAsync();
        Assert.False(owner.Composition.ServicesPublished);
    });
}
