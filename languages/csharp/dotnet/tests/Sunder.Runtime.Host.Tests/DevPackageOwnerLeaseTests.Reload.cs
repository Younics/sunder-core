using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class DevPackageOwnerLeaseTests
{
    [Fact]
    public async Task OwnerReloadStagesAnExactRevisionWithoutReplacingTheOwnerAndReplaysOnlyItsReceipt()
    {
        await using var fixture = CreateFixture();
        var folder = CreatePackageLayout(fixture.Paths.RootPath, "reload", "startup.counted.package", "1.0.0").InstallPath;
        const string token = "explicit-reload-owner-token-aaaaaaaaa";
        await fixture.Host.LoadInstalledPackagesAsync();
        var lease = await fixture.Host.ReplaceDevPackageOwnerAsync("owner", Mutation(fixture.Host.RuntimeInstanceId, token, "load", 1, folder, false));
        var old = fixture.Host.ActiveSession.LoadedPackageMap["startup.counted.package"];
        var generation = fixture.Host.SessionGeneration;
        var identity = ChangeReloadOutput(folder);
        var request = new DevPackageOwnerReloadStageRequest(fixture.Host.RuntimeInstanceId, token, lease.Revision, "reload", "startup.counted.package", identity);
        var prepared = await fixture.Host.StageOwnerReloadAsync("owner", request);
        Assert.Equal(prepared, await fixture.Host.StageOwnerReloadAsync("owner", request));
        Assert.Equal(identity, prepared.SourceContentIdentity);
        Assert.Equal(generation, fixture.Host.SessionGeneration);
        Assert.Same(old.Worker, fixture.Host.ActiveSession.LoadedPackageMap["startup.counted.package"].Worker);
        Assert.False(old.Worker.GenerationCompletion.IsCompleted);
        await Assert.ThrowsAsync<RuntimeConflictException>(() => fixture.Host.StageOwnerReloadAsync("owner", request with { SourceContentIdentity = new('f', 64) }));
        await Assert.ThrowsAsync<RuntimeAuthenticationException>(() => fixture.Host.CommitPackageLifecycleStageAsync(prepared.Stage.StageId!));
        var commit = new DevPackageOwnerReloadCommitRequest(fixture.Host.RuntimeInstanceId, token, prepared.Stage.StageId!);
        var result = await fixture.Host.CommitOwnerReloadAsync("owner", commit);
        Assert.Equal(generation + 1, result.Snapshot.SessionGeneration);
        Assert.Equal(identity, Assert.Single(result.Snapshot.SessionPackages).SourceContentIdentity);
        Assert.True(old.Worker.GenerationCompletion.IsCompleted);
        Assert.True(fixture.Host.ContainsDevPackageOwner("owner"));
        Assert.Equal(lease.MutationId, (await fixture.Host.HeartbeatDevPackageOwnerAsync("owner", new(fixture.Host.RuntimeInstanceId, token))).MutationId);
        Assert.Equal(result, await fixture.Host.CommitOwnerReloadAsync("owner", commit));
        Assert.Equal(generation + 1, fixture.Host.SessionGeneration);
        await Assert.ThrowsAsync<RuntimeAuthenticationException>(() => fixture.Host.CommitOwnerReloadAsync("foreign", commit));
        await Assert.ThrowsAsync<RuntimeAuthenticationException>(() => fixture.Host.CommitOwnerReloadAsync("owner", commit with { OwnerToken = new('b', 40) }));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("package")]
    [InlineData("cancel")]
    [InlineData("discard")]
    [InlineData("coowner")]
    public async Task RejectedOrWithdrawnOwnerReloadKeepsTheLastGoodWorkerAndNeverRetargets(string boundary)
    {
        await using var fixture = CreateFixture();
        var folder = CreatePackageLayout(fixture.Paths.RootPath, "reload", "startup.counted.package", "1.0.0").InstallPath;
        const string token = "rejected-reload-owner-token-aaaaaaaaa";
        await fixture.Host.LoadInstalledPackagesAsync();
        await fixture.Host.ReplaceDevPackageOwnerAsync("owner", Mutation(fixture.Host.RuntimeInstanceId, token, "load", 1, folder, false));
        var old = fixture.Host.ActiveSession.LoadedPackageMap["startup.counted.package"];
        var generation = fixture.Host.SessionGeneration;
        var identity = ChangeReloadOutput(folder);
        var request = new DevPackageOwnerReloadStageRequest(fixture.Host.RuntimeInstanceId, token, 1, "reload", "startup.counted.package", identity);
        if (boundary == "identity") await Assert.ThrowsAsync<RuntimeConflictException>(() => fixture.Host.StageOwnerReloadAsync("owner", request with { SourceContentIdentity = new('f', 64) }));
        else if (boundary == "package") await Assert.ThrowsAsync<RuntimeConflictException>(() => fixture.Host.StageOwnerReloadAsync("owner", request with { PackageId = "foreign.package" }));
        else if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Host.StageOwnerReloadAsync("owner", request, new(true)));
        else
        {
            var stage = await fixture.Host.StageOwnerReloadAsync("owner", request);
            var commit = new DevPackageOwnerReloadCommitRequest(fixture.Host.RuntimeInstanceId, token, stage.Stage.StageId!);
            if (boundary == "discard") await fixture.Host.DiscardOwnerReloadAsync("owner", commit);
            else await fixture.Host.ReplaceDevPackageOwnerAsync("coowner", Mutation(fixture.Host.RuntimeInstanceId, token, "load", 1, folder, false));
            await Assert.ThrowsAsync<RuntimeConflictException>(() => fixture.Host.CommitOwnerReloadAsync("owner", commit));
        }
        Assert.Equal(generation, fixture.Host.SessionGeneration);
        Assert.Same(old.Worker, fixture.Host.ActiveSession.LoadedPackageMap["startup.counted.package"].Worker);
        Assert.False(old.Worker.GenerationCompletion.IsCompleted);
        Assert.True(fixture.Host.ContainsDevPackageOwner("owner"));
    }

    [Fact]
    public async Task OwnerReloadRequiresTheExactAppRetirementAcknowledgementBeforeCommit()
    {
        using var policy = new ActivePolicyTestContext();
        await using var fixture = CreateFixture(appPolicyFactory: (state, protocol, snapshots, readiness) => new(policy.Paths, protocol, snapshots,
            new(policy.Cache), policy.Cache, policy.Clock, policy.Time, new RetirementProcess(), NullLogger<AppProcessPolicySessions>.Instance, state, readiness));
        var folder = CreateRequiredAppPackage(fixture.Paths.RootPath, web: true);
        const string token = "app-reload-owner-token-aaaaaaaaaaaaaa";
        await fixture.Host.LoadInstalledPackagesAsync();
        await fixture.Host.ReplaceDevPackageOwnerAsync("owner", Mutation(fixture.Host.RuntimeInstanceId, token, "load", 1, folder, false));
        var (session, permit, _) = await OpenTrackedAppAsync(fixture.Host);
        var old = fixture.Host.ActiveSession.LoadedPackageMap["startup.counted.package"].Worker;
        var path = Path.Combine(folder, SunderPackageFormat.ManifestPath);
        var manifest = JsonNode.Parse(File.ReadAllBytes(path))!;
        manifest["provides"] = new JsonArray();
        var appUse = manifest["usesContracts"]!.AsArray().Single(value => value!["role"]!.GetValue<string>() == "app");
        manifest["usesContracts"]!.AsArray().Remove(appUse);
        File.WriteAllText(path, manifest.ToJsonString());
        var identity = ChangeReloadOutput(folder);
        var request = new DevPackageOwnerReloadStageRequest(fixture.Host.RuntimeInstanceId, token, 1, "reload", "startup.counted.package", identity);
        var first = await fixture.Host.StageOwnerReloadAsync("owner", request);
        Assert.Equal(permit.AppActivationId, Assert.Single(first.Stage.Retirements).AppActivationId);
        await Assert.ThrowsAsync<RuntimeRequiredProviderException>(() => fixture.Host.CommitOwnerReloadAsync("owner",
            new(fixture.Host.RuntimeInstanceId, token, first.Stage.StageId!)));
        Assert.False(old.GenerationCompletion.IsCompleted);
        var next = await fixture.Host.StageOwnerReloadAsync("owner", request with { MutationId = "next-explicit-reload" });
        await fixture.Host.AppPolicies!.RetireActivationAsync(new(session, permit.AppActivationId!.Value, permit.PermitId!), new(123, null), default);
        var result = await fixture.Host.CommitOwnerReloadAsync("owner", new(fixture.Host.RuntimeInstanceId, token, next.Stage.StageId!));
        Assert.Equal(identity, result.SourceContentIdentity);
        Assert.True(old.GenerationCompletion.IsCompleted);
    }

    private static string ChangeReloadOutput(string folder)
    {
        var path = Path.Combine(folder, SunderPackageFormat.ManifestPath);
        var manifest = JsonNode.Parse(File.ReadAllBytes(path))!;
        manifest["name"] = "Explicitly rebuilt output";
        File.WriteAllText(path, manifest.ToJsonString());
        CanonicalPackageTestBuilder.WriteContentIndex(folder);
        return PackageSessionPreparer.ComputeContentIdentity(folder);
    }
}
