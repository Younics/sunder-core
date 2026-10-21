using System.Security.Cryptography;
using System.Text.Json;
using Sunder.App.Services;
using Sunder.Core.Tests;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AgentPackageReleaseSmokeTests
{
    [ExternalArtifactFact("SUNDER_REQUIRE_AGENT_UI_SNAPSHOT", "SUNDER_AGENT_UI_SNAPSHOT_DIR")]
    [Trait("Category", "ExternalArtifact")]
    public async Task RuntimeProducedAgentSnapshot_MaterializesAndActivatesInApp()
    {
        var snapshotDirectory = Environment.GetEnvironmentVariable("SUNDER_AGENT_UI_SNAPSHOT_DIR");
        Assert.False(string.IsNullOrWhiteSpace(snapshotDirectory), "SUNDER_AGENT_UI_SNAPSHOT_DIR is required by the selected artifact lane.");

        var metadata = JsonSerializer.Deserialize<AgentAppSnapshotMetadata>(
            await File.ReadAllTextAsync(Path.Combine(snapshotDirectory!, "metadata.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(metadata);
        var baseline = AgentCurrentBaseline.Load();
        Assert.Equal(baseline.InventorySha256, metadata.InventorySha256);
        Assert.Equal(baseline.ContractsSha256, metadata.ContractsSha256);
        Assert.Equal(AgentCurrentBaseline.AgentId, metadata.Package.PackageId);
        Assert.Equal(metadata.Package.PackageId, metadata.Snapshot.PackageId);
        Assert.Equal(baseline.PackageVersion, metadata.Package.Version);
        Assert.Contains(metadata.Package.PackageId, baseline.AppPackageIds);
        var snapshotPath = Path.Combine(snapshotDirectory!, "younics.ai.agent.snapshot");
        await using var snapshotStream = File.OpenRead(snapshotPath);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(snapshotStream)).ToLowerInvariant();
        Assert.Equal(metadata.Snapshot.ContentHash, actualHash);

        var sessionRoot = Path.Combine(Path.GetTempPath(), "sunder-agent-app-release-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionRoot);
        var host = new PackageViewHostService(
            new AppPackageViewRegistry(),
            [],
            [],
            [],
            sessionRoot,
            downloadPackageUiSnapshotAsync: async (_, destination, cancellationToken) =>
            {
                await using var source = new FileStream(
                    snapshotPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(destination, cancellationToken);
            },
            uiDispatcher: new ImmediateUiDispatcher());

        try
        {
            await host.ApplyPackageDeltaAsync([metadata.Package], [metadata.Snapshot]);

            var activePackage = Assert.Single(host.FilterEnabledPackages([metadata.Package]));
            Assert.Equal(1, host.LoadedPackageCount);
            Assert.Contains(activePackage.Views, view => view.ViewId == "younics.ai.agent.chat");
        }
        finally
        {
            await host.DisposeAsync();
            TryDelete(sessionRoot);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record AgentAppSnapshotMetadata(
        ActivePackageDescriptor Package,
        PackageUiSnapshotDescriptor Snapshot,
        string InventorySha256,
        string ContractsSha256);

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> action) => action();

        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());

        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }
}
