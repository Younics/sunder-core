using System.Diagnostics;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcImportedBuildTests
{
    [ExternalArtifactFact("SUNDER_REQUIRE_NODE_OUTPUT_LOCK_INTEROP")]
    [Trait("Category", "ExternalArtifact")]
    public async Task NodePackageApi_CandidateCliBuildPackAndRoleIsolationPassCanonicalInspection()
        => await VerifyNodeCandidateImportsAsync(provider: false);

    [ExternalArtifactFact("SUNDER_REQUIRE_NODE_OUTPUT_LOCK_INTEROP")]
    [Trait("Category", "ExternalArtifact")]
    public async Task NodePackageApi_ProviderCandidateCliBuildPackAndRoleIsolationPassCanonicalInspection()
        => await VerifyNodeCandidateImportsAsync(provider: true);

    private async Task VerifyNodeCandidateImportsAsync(bool provider)
    {
        var candidate = await CreateLockedCandidateAsync();
        var cli = CanonicalInspectorCli();
        var helper = Path.Combine(RepositoryRoot(), "languages/typescript/node/packages/package-tool/dist-test/test/package-import-child.js");
        Assert.True(File.Exists(cli)); Assert.True(File.Exists(helper));
        var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["SUNDER_CLI"] = cli;
        foreach (var argument in new[] { helper, _root, candidate, Path.Combine(_root, "node-import-cache"), "app" }) start.ArgumentList.Add(argument);
        if (provider) start.ArgumentList.Add("provider");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4)); Assert.True(process.ExitCode == 0, await stdout + await stderr); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        var archive = Path.Combine(_root, "node-import/dist/example.owner.1.0.0.sunderpkg");
        var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archive, Path.Combine(_root, "node-import-verified"));
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        var manifest = validation.Manifest!;
        Assert.Equal(2, manifest.Targets!.Count);
        Assert.Equal(provider ? "required" : "optional", Assert.Single(manifest.DependsOn!).Presence);
        Assert.Equal("imported", Assert.Single(manifest.ContractBundles!)!.Source);
        if (provider)
        {
            Assert.Empty(manifest.UsesContracts!);
            Assert.Empty(manifest.PackageApiImports!);
            Assert.Equal("runtime", Assert.Single(manifest.Provides!)!.Role);
            var imported = Assert.Single(manifest.ProviderImports!)!;
            Assert.Equal("extension", imported.Key);
            Assert.Equal(ContractId + ".provider", Assert.Single(imported.ProviderRefs!)!.ProviderId);
        }
        else
        {
            Assert.Equal(new[] { "app", "runtime" }, manifest.UsesContracts!.Select(use => use!.Role).Order(StringComparer.Ordinal));
            Assert.All(manifest.UsesContracts!, use => Assert.False(use!.Required));
            Assert.Empty(manifest.Provides!);
            Assert.Empty(manifest.ProviderImports!);
            var import = Assert.Single(manifest.PackageApiImports!)!;
            Assert.Equal("optional", import.Presence);
            Assert.Equal(new[] { "app", "runtime" }, import.UseRefs!.Select(use => use!.Role).Order(StringComparer.Ordinal));
        }
        var original = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        var node = SunderRpcAuthoringDocuments.ReadContractLock(Path.Combine(_root, "node-import/sunder-contracts.lock.json"));
        if (!provider) Assert.Equal(new[] { "app", "runtime" }, node.PackageImports[DependencyId].ConsumerRoles);
        Assert.Equal(SunderRpcAuthoringDocuments.ValidatePackagePublicInventory(original.PackageArtifacts[DependencyId]).ToCanonicalUtf8(),
            SunderRpcAuthoringDocuments.ValidatePackagePublicInventory(node.PackageArtifacts[DependencyId]).ToCanonicalUtf8());
    }
}
