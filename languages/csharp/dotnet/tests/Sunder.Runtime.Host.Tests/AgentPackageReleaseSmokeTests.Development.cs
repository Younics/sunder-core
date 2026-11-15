using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Core.Tests;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.Host.TestSupport;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class AgentPackageReleaseSmokeTests
{
    [ExternalArtifactFact("SUNDER_REQUIRE_AGENT_PACKAGE_RELEASE", "SUNDER_AGENT_PACKAGE_ARCHIVE_DIR", "SUNDER_AGENT_PLATFORM_CATALOG", "SUNDER_AGENT_UI_SNAPSHOT_DIR")]
    [Trait("Category", "ExternalArtifact")]
    [Trait("Capability", "Process")]
    public async Task DevelopmentArchives_ActivateUnloadReloadAndReplaceAsOneFamily()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var baseline = AgentCurrentBaseline.Load();
        var archives = Directory.GetFiles(Environment.GetEnvironmentVariable("SUNDER_AGENT_PACKAGE_ARCHIVE_DIR")!, "*.sunderpkg")
            .ToDictionary(path => baseline.VerifyArchive(path), Path.GetFullPath, StringComparer.Ordinal);
        Assert.Equal(baseline.Packages.Select(package => package.PackageId).Order(StringComparer.Ordinal), archives.Keys.Order(StringComparer.Ordinal));
        var catalog = Environment.GetEnvironmentVariable("SUNDER_AGENT_PLATFORM_CATALOG")!;
        var inputs = archives.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => AgentFamilyArchiveInput.Read(pair.Key, pair.Value)).ToArray();
        await using var host = await DevelopmentPackageFamilyHost.CreateAsync(archives, catalog, archives.Keys.Order(StringComparer.Ordinal).ToArray(), deadline.Token);
        await host.LoadAsync(deadline.Token);
        VerifyFamily();
        var initial = host.CaptureWorkers();
        var unload = await host.Sessions.LoadStartupDevPackagesAsync([], deadline.Token);
        Assert.True(unload.Success, string.Join(" | ", unload.Errors));
        Assert.Empty(host.Owner.State.ActiveSession.LoadedPackageMap);
        await DevelopmentPackageFamilyHost.AssertStoppedAsync(initial);
        await host.LoadAsync(deadline.Token);
        VerifyFamily();
        var reloaded = host.CaptureWorkers();
        var stage = await host.Sessions.StageAsync(new(archives.Keys.Order(StringComparer.Ordinal).ToArray()), deadline.Token);
        Assert.True(stage.Success, string.Join(" | ", stage.Errors));
        var staged = Assert.IsType<ActivePackageSession>(host.Sessions.GetStagedSession(stage.StageId!));
        var replacements = staged.LoadedPackageMap.ToDictionary(pair => pair.Key, pair => Assert.IsType<ProcessRuntimeWorker>(pair.Value.Worker), StringComparer.Ordinal);
        Assert.Equal(15, replacements.Count);
        Assert.All(replacements, pair =>
        {
            Assert.NotSame(host.Owner.State.ActiveSession.LoadedPackageMap[pair.Key].Worker, pair.Value);
            Assert.False(pair.Value.ActivationCompletion.IsCompleted);
        });
        var replaced = await host.Sessions.CommitStageAsync(stage.StageId!, deadline.Token);
        Assert.True(replaced.Success, string.Join(" | ", replaced.Errors));
        // Staging prepares the replacement objects; commit starts/activates them.
        // Retain native identities only after that boundary, never assume a staged PID.
        Assert.All(replacements, pair => Assert.Same(pair.Value, host.Owner.State.ActiveSession.LoadedPackageMap[pair.Key].Worker));
        Assert.Equal(15, host.CaptureWorkers().Length);
        await DevelopmentPackageFamilyHost.AssertStoppedAsync(reloaded);
        VerifyFamily();

        var output = Environment.GetEnvironmentVariable("SUNDER_AGENT_UI_SNAPSHOT_DIR")!;
        Directory.CreateDirectory(output);
        var snapshot = Assert.Single(host.Ui.GetActiveSnapshots(PackageTargetSelection.GetCurrentRuntimeIdentifier()), source => source.PackageId == AgentPackageId);
        var package = Assert.Single(host.Sessions.GetActivePackages(), package => package.PackageId == AgentPackageId);
        using var lease = host.Ui.AcquireCurrent(snapshot.SnapshotId);
        Assert.NotNull(lease);
        var snapshotPath = Path.Combine(output, "younics.ai.agent.snapshot");
        await using (var destination = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await lease.Stream.CopyToAsync(destination, deadline.Token);
        Assert.Equal(snapshot.ContentHash, AgentFamilyArtifactHandoff.Hash(snapshotPath));
        var handoff = new AgentFamilyArtifactHandoff(2, AgentFamilyArtifactHandoff.DevelopmentProfile, false, package, snapshot,
            baseline.InventorySha256, baseline.ContractsSha256, AgentFamilyArtifactHandoff.Hash(catalog), inputs);
        handoff.VerifyInputs(archives, catalog, baseline.InventorySha256, baseline.ContractsSha256);
        await using var metadata = new FileStream(Path.Combine(output, "metadata.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(metadata, handoff, AgentFamilyArtifactHandoff.Json, deadline.Token);

        void VerifyFamily()
        {
            Assert.Equal(15, host.Owner.State.ActiveSession.LoadedPackageMap.Count);
            Assert.Equal(baseline.AppPackageIds, host.Ui.GetActiveSnapshots(PackageTargetSelection.GetCurrentRuntimeIdentifier()).Select(source => source.PackageId).Order(StringComparer.Ordinal));
            baseline.VerifyProviders(host.Services.GetRequiredService<RuntimeRpcCatalog>().GetSnapshot().Providers);
            VerifyReleasedSqliteBytes(archives.Values.ToArray(), baseline.PackageVersion, host.Folders[SemanticMemoryPackageId]);
        }
    }
}
