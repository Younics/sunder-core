using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Core.TestSupport;
using Sunder.Core.Tests;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.Host.TestSupport;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.App.Integration.Tests;

[Collection("App/Runtime integration")]
public sealed class AgentPackageReleaseSmokeTests
{
    [ExternalArtifactFact("SUNDER_REQUIRE_AGENT_UI_SNAPSHOT", "SUNDER_AGENT_UI_SNAPSHOT_DIR", "SUNDER_AGENT_PACKAGE_ARCHIVE_DIR", "SUNDER_AGENT_PLATFORM_CATALOG")]
    [Trait("Category", "ExternalArtifact")]
    [Trait("Capability", "Process")]
    public async Task RuntimeProducedAgentSnapshot_MaterializesAndActivatesInSupervisedApp()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var baseline = AgentCurrentBaseline.Load();
        var archives = Directory.GetFiles(Environment.GetEnvironmentVariable("SUNDER_AGENT_PACKAGE_ARCHIVE_DIR")!, "*.sunderpkg")
            .ToDictionary(path => baseline.VerifyArchive(path), Path.GetFullPath, StringComparer.Ordinal);
        Assert.Equal(baseline.Packages.Select(package => package.PackageId).Order(StringComparer.Ordinal), archives.Keys.Order(StringComparer.Ordinal));
        var catalog = Environment.GetEnvironmentVariable("SUNDER_AGENT_PLATFORM_CATALOG")!;
        var snapshotDirectory = Environment.GetEnvironmentVariable("SUNDER_AGENT_UI_SNAPSHOT_DIR")!;
        var metadata = JsonSerializer.Deserialize<AgentFamilyArtifactHandoff>(
            await File.ReadAllBytesAsync(Path.Combine(snapshotDirectory, "metadata.json"), deadline.Token), AgentFamilyArtifactHandoff.Json)!;
        metadata.VerifyInputs(archives, catalog, baseline.InventorySha256, baseline.ContractsSha256);
        Assert.Equal(AgentCurrentBaseline.AgentId, metadata.Package.PackageId);
        Assert.Equal(baseline.PackageVersion, metadata.Package.Version);
        var snapshotPath = Path.Combine(snapshotDirectory, "younics.ai.agent.snapshot");
        Assert.Equal(metadata.Snapshot.ContentHash, AgentFamilyArtifactHandoff.Hash(snapshotPath));

        // Snapshot aliases/generations belong to the exporting Runtime. Recreate
        // admission from the identical archive/catalog inputs, never relabel an
        // expired alias or synthesize an App permit for offline bytes.
        await using var host = await DevelopmentPackageFamilyHost.CreateAsync(archives, catalog, [AgentCurrentBaseline.AgentId], deadline.Token);
        await host.LoadAsync(deadline.Token);
        var fresh = Assert.Single(host.Ui.GetActiveSnapshots(PackageTargetSelection.GetCurrentRuntimeIdentifier()));
        var package = Assert.Single(host.Sessions.GetActivePackages());
        Assert.Equal(metadata.Snapshot.ContentHash, fresh.ContentHash);
        Assert.Equal(metadata.Snapshot.SourceContentIdentity, fresh.SourceContentIdentity);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(metadata.Snapshot.Target, AgentFamilyArtifactHandoff.Json),
            JsonSerializer.SerializeToElement(fresh.Target, AgentFamilyArtifactHandoff.Json)), "The fresh App target differs from the exported artifact target.");
        var connectionPath = Path.Combine(host.RootPath, "app-connection.json");
        RuntimeConnectionInfoStore.Save(host.Connection, connectionPath);
        var config = Path.Combine(host.RootPath, "app.json");
        await File.WriteAllBytesAsync(config, JsonSerializer.SerializeToUtf8Bytes(new
        {
            connectionPath, package, snapshot = fresh, snapshotPath,
            sessionRoot = Path.Combine(host.RootPath, "app-session"),
            runtimeRoot = host.Paths.RootPath,
        }, AgentFamilyArtifactHandoff.Json), deadline.Token);
        var assembly = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sunder-policy-app-target-path.txt")).Trim();
        var executable = Path.Combine(Path.GetDirectoryName(assembly)!, OperatingSystem.IsWindows() ? "Sunder.App.exe" : "Sunder.App");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("SUNDER_", StringComparison.OrdinalIgnoreCase)
            || key is "GH_TOKEN" or "GITHUB_TOKEN" || key.StartsWith("DOTNET_STARTUP_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["DOTNET_STARTUP_HOOKS"] = Path.Combine(AppContext.BaseDirectory, "agent-family-app-hook", "Sunder.App.Tests.dll");
        start.Environment["SUNDER_TEST_AGENT_FAMILY_APP_CONFIG"] = config;
        start.Environment["SUNDER_APP_STATE_ROOT"] = Path.Combine(host.RootPath, "app-state");
        start.Environment[RuntimeLocalState.StateRootEnvironmentVariable] = host.Paths.RootPath;
        start.Environment["SUNDER_HOST_STATE_ROOT"] = Path.Combine(host.RootPath, "host-state");
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        await using var process = CapturedProcess.Start(start, 256 * 1024);
        await process.WaitForOutputAsync(output => output.Contains("agent-family-app: materialized-and-activated", StringComparison.Ordinal), TimeSpan.FromSeconds(90), deadline.Token);
        var policyRoot = Path.Combine(host.Paths.RootPath, RuntimeV1StateDescriptor.AppPolicySessionsDirectory);
        using (var proof = JsonDocument.Parse(await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(policyRoot, "*.json")), deadline.Token)))
        {
            Assert.Equal(process.Id, proof.RootElement.GetProperty("process").GetProperty("processId").GetInt32());
            Assert.Equal(process.Id, proof.RootElement.GetProperty("peer").GetProperty("processId").GetInt32());
            var loaded = Assert.Single(proof.RootElement.GetProperty("loaded").EnumerateArray());
            Assert.Equal(AgentCurrentBaseline.AgentId, loaded.GetProperty("package").GetProperty("packageId").GetString());
            Assert.True(loaded.GetProperty("development").GetBoolean());
            Assert.True(!loaded.TryGetProperty("registryOrigin", out var origin) || origin.ValueKind == JsonValueKind.Null);
            Assert.NotEmpty(proof.RootElement.GetProperty("activations").EnumerateArray());
        }
        process.Process.StandardInput.Close();
        var result = await process.EnsureSuccessAsync(TimeSpan.FromSeconds(45), deadline.Token);
        Console.WriteLine(result.StandardOutput);
        Assert.Contains("agent-family-app: retired", result.StandardOutput, StringComparison.Ordinal);
        await host.Services.GetRequiredService<AppProcessPolicySessions>().EnforceAsync(deadline.Token);
        Assert.Empty(Directory.GetFiles(policyRoot, "*.json"));
        Assert.Empty(await host.Services.GetRequiredService<InstalledPackageLifecycleService>().GetInstalledAsync(deadline.Token));
    }
}
