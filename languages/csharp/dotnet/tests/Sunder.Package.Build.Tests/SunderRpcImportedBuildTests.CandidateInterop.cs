using System.Diagnostics;
using System.IO.Compression;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcImportedBuildTests
{
    [ExternalArtifactFact("SUNDER_REQUIRE_NODE_OUTPUT_LOCK_INTEROP")]
    [Trait("Category", "ExternalArtifact")]
    public async Task CandidateInterop_NodeUsesTheCanonicalCliInspectorAndReproducesCSharpCacheBytes()
    {
        var archive = await CreateLockedCandidateAsync();
        var helper = Path.Combine(RepositoryRoot(), "languages/typescript/node/packages/package-tool/dist-test/test/contract-candidate-child.js");
        var cli = CanonicalInspectorCli();
        Assert.True(File.Exists(helper), "Build the Node test helpers before selecting candidate interop.");
        Assert.True(File.Exists(cli), "Build Sunder.Cli before selecting canonical candidate interop.");
        var before = File.ReadAllBytes(LockPath);
        var nodeCache = Path.Combine(_root, "node-candidate-cache");
        await Node(archive, nodeCache, success: true);
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException("Offline restore attempted HTTP.")));
        var csharpCache = Path.Combine(_root, "csharp-candidate-cache");
        Assert.Equal(1, await new SunderRpcContractRestore(http).RestoreAsync(LockPath, csharpCache, [archive]));
        foreach (var file in Directory.GetFiles(csharpCache))
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(nodeCache, Path.GetFileName(file))));
        Assert.Equal(before, File.ReadAllBytes(LockPath));
        var cacheBefore = Snapshot(nodeCache);
        await Node(archive, nodeCache, success: true);
        Assert.Equal(cacheBefore, Snapshot(nodeCache));

        var localLock = ReadJson(LockPath);
        localLock["packageArtifacts"]![DependencyId]!.AsObject().Remove("registryOrigin");
        WriteJson(LockPath, localLock);
        var localBytes = File.ReadAllBytes(LockPath);
        await Node(archive, Path.Combine(_root, "node-local-candidate-cache"), success: true);
        Assert.Equal(localBytes, File.ReadAllBytes(LockPath));
        var nodeSelection = SunderRpcAuthoringDocuments.ReadContractLock(Path.Combine(_root, "node-local-authoring", "sunder-contracts.lock.json"));
        Assert.Null(nodeSelection.PackageArtifacts[DependencyId].RegistryOrigin);
        Assert.Equal(SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PackageArtifacts[DependencyId].SelectedPackageContentIdentity,
            nodeSelection.PackageArtifacts[DependencyId].SelectedPackageContentIdentity);
        File.WriteAllBytes(LockPath, before);

        var invalid = Path.Combine(_root, "invalid-candidate.sunderpkg"); File.Copy(archive, invalid);
        using (var zip = ZipFile.Open(invalid, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(zip.CreateEntry("payload/shared/unindexed.txt").Open())) writer.Write("unindexed");
        var rejectedCache = Path.Combine(_root, "rejected-candidate-cache");
        await Node(invalid, rejectedCache, success: false);
        Assert.False(Directory.Exists(rejectedCache)); Assert.Equal(before, File.ReadAllBytes(LockPath));

        var changedLock = ReadJson(LockPath);
        var artifact = changedLock["packageArtifacts"]![DependencyId]!;
        artifact["selectedPackageContentIdentity"] = new string('f', 64);
        artifact["contracts"]![ContractId]!["selectedAuthorityContentIdentity"] = new string('f', 64);
        artifact["publicInventorySha256"] = InventorySha256(DependencyId, artifact);
        WriteJson(LockPath, changedLock);
        var changedBytes = File.ReadAllBytes(LockPath);
        await Node(archive, rejectedCache, success: false);
        Assert.False(Directory.Exists(rejectedCache)); Assert.Equal(changedBytes, File.ReadAllBytes(LockPath));

        async Task Node(string candidate, string cache, bool success)
        {
            // This helper's registry-parity leg explicitly tests a cold restore.
            // Keep the candidate cache intact for the repeat/no-op assertion above.
            var registryComparisonCache = cache + "-registry";
            if (Directory.Exists(registryComparisonCache)) Directory.Delete(registryComparisonCache, recursive: true);
            var info = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            info.Environment["SUNDER_CLI"] = cli;
            foreach (var argument in new[] { helper, _root, candidate, cache }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                var output = await stdout + await stderr;
                Assert.True(success ? process.ExitCode == 0 : process.ExitCode != 0, output);
                Assert.Contains(success ? "candidate-restored" : "package-api.public-inventory-invalid", output, StringComparison.Ordinal);
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
    }

    private static string CanonicalInspectorCli()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var directory = Path.Combine(RepositoryRoot(), "languages/csharp/dotnet/host/Sunder.Cli/bin", configuration, "net10.0");
        var cli = Path.Combine(directory, OperatingSystem.IsWindows() ? "sunder.exe" : "sunder");
        Assert.True(File.Exists(cli), "Build Sunder.Cli before canonical candidate interop.");
        foreach (var assembly in new[] { typeof(Sunder.Package.Format.SunderPackageArchiveInspector).Assembly,
                     typeof(Sunder.Sdk.Rpc.SunderRpcContractDescriptor).Assembly })
        {
            var dependency = Path.Combine(directory, Path.GetFileName(assembly.Location));
            Assert.True(File.Exists(dependency) && File.ReadAllBytes(assembly.Location).AsSpan().SequenceEqual(File.ReadAllBytes(dependency)),
                $"Canonical inspector has stale {assembly.GetName().Name} bytes; rebuild Sunder.Cli with the qualified Core inputs.");
        }
        return cli;
    }
}
