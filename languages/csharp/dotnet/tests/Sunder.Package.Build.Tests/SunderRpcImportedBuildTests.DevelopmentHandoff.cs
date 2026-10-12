using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcImportedBuildTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevelopmentHandoff_ExportsVerifiedLocalSelectionBesideAggregateAndClearsItForRegistry(bool defaultLockPath)
    {
        var original = ReadJson(LockPath);
        var selected = original.DeepClone().AsObject();
        selected["packageArtifacts"]![DependencyId]!.AsObject().Remove("registryOrigin");
        WriteJson(LockPath, selected);
        var cacheBefore = Snapshot(_cache);
        var manifest = BuildManifest(Generate());
        var leaf = EmitLeaf(manifest);
        var aggregate = CreateAggregate(leaf);
        aggregate.ProjectDirectory = _root;
        aggregate.ContractLockPath = defaultLockPath ? null : LockPath;
        aggregate.ContractCacheRoot = _cache;
        AssertSuccess(aggregate);
        var path = aggregate.OutputPath + SunderPackageFormat.DevelopmentImportsSuffix;
        using (var document = JsonDocument.Parse(File.ReadAllBytes(path)))
        {
            var handoff = document.RootElement;
            Assert.Equal("local", handoff.GetProperty("sourceKind").GetString());
            Assert.Equal(JsonValueKind.Null, handoff.GetProperty("registryOrigin").ValueKind);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(
                Path.Combine(aggregate.OutputPath, SunderPackageFormat.ManifestPath)))), handoff.GetProperty("manifestSha256").GetString());
            var imported = Assert.Single(handoff.GetProperty("inventories").EnumerateArray());
            var inventory = SunderRpcAuthoringDocuments.ValidatePackagePublicInventory(
                SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PackageArtifacts[DependencyId]);
            Assert.True(JsonElement.DeepEquals(inventory.Payload, imported.GetProperty("artifact").GetProperty("inventory")));
            Assert.Equal(inventory.PublicInventorySha256, imported.GetProperty("artifact").GetProperty("publicInventorySha256").GetString());
            Assert.Empty(imported.GetProperty("authorityRequirements").EnumerateArray());
        }
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(aggregate.OutputPath);
        Assert.True(validation.Success, string.Join(" | ", validation.Errors));
        Assert.Equal(cacheBefore, Snapshot(_cache));

        WriteJson(LockPath, original);
        AssertSuccess(aggregate);
        Assert.Equal("null\n", File.ReadAllText(path));
        AssertSuccess(new CleanSunderPackageOutputTask
        {
            BuildEngine = new BuildEngine(), OutputPath = aggregate.OutputPath,
            OutputDiscoveryRoot = aggregate.OutputDiscoveryRoot, ProjectDirectory = aggregate.ProjectDirectory,
            TargetDirectory = aggregate.TargetDirectory,
        });
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DevelopmentHandoff_PreservesExactOriginalLocalCatalogAndRejectsChangedCache()
    {
        var fixture = await PreparePlatformProviderAsync();
        var selected = ReadJson(LockPath);
        foreach (var import in selected["platformImports"]!.AsObject()) import.Value!.AsObject().Remove("registryOrigin");
        WriteJson(LockPath, selected);
        var manifest = BuildManifest(Generate());
        using var handoff = JsonDocument.Parse(DevelopmentImportHandoff.Create(manifest.ManifestOutputPath, LockPath, _cache, false));
        Assert.Empty(handoff.RootElement.GetProperty("inventories").EnumerateArray());
        Assert.Equal(fixture.Envelope, Convert.FromBase64String(Assert.Single(handoff.RootElement.GetProperty("catalogs").EnumerateArray()).GetString()!));
        File.AppendAllText(SunderRpcPlatformCatalogCache.CatalogPath(_cache, fixture.Requirement.ContentIdentity), " ");
        Assert.Throws<InvalidDataException>(() => DevelopmentImportHandoff.Create(manifest.ManifestOutputPath, LockPath, _cache, false));
    }
}
