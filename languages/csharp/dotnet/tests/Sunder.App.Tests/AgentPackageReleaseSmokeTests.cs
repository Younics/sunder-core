using System.Security.Cryptography;
using System.Text.Json;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AgentPackageReleaseSmokeTests
{
    [Fact]
    public async Task RuntimeProducedAgentSnapshot_MaterializesAndActivatesInApp()
    {
        var snapshotDirectory = Environment.GetEnvironmentVariable("SUNDER_AGENT_UI_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(snapshotDirectory)) return;

        var metadata = JsonSerializer.Deserialize<AgentAppSnapshotMetadata>(
            await File.ReadAllTextAsync(Path.Combine(snapshotDirectory, "metadata.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(metadata);
        var snapshotPath = Path.Combine(snapshotDirectory, "sunder.package.agent.snapshot");
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
            Assert.Contains(activePackage.Views, view => view.ViewId == "sunder.package.agent.chat");
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
        PackageUiSnapshotDescriptor Snapshot);

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
