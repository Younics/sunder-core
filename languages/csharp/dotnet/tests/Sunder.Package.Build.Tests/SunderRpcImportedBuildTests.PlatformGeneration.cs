using System.Text.Json.Nodes;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcImportedBuildTests
{
    [Fact]
    public async Task PlatformGeneration_CompilesProviderAndPacksWithoutConsumerUsesOrPackageDependencies()
    {
        var fixture = await PreparePlatformProviderAsync();
        var before = File.ReadAllBytes(LockPath);
        var generation = CreateGeneration();
        AssertSuccess(generation);
        Assert.Single(generation.RpcProviders); Assert.Empty(generation.RpcUses);
        var manifestTask = BuildManifest(generation); var manifest = ReadManifest(manifestTask.ManifestOutputPath);
        var bundle = Assert.Single(manifest.ContractBundles!)!;
        Assert.Equal("platform", bundle.AuthorityKind); Assert.Equal("imported", bundle.Source);
        Assert.Equal(fixture.Requirement.ContentIdentity, bundle.SelectedAuthorityContentIdentity);
        Assert.Empty(manifest.DependsOn!); Assert.Empty(manifest.UsesContracts!); Assert.Empty(manifest.PackageArtifactImports!);
        Assert.Equal("stackContributor", Assert.Single(manifest.PlatformImports!)!.Key);
        Assert.Single(Assert.Single(manifest.PlatformImports!)!.ProviderRefs!);
        var leaf = EmitLeaf(manifestTask); var aggregate = CreateAggregate(leaf); AssertSuccess(aggregate);
        var pack = new PackSunderPackageTask
        {
            BuildEngine = new BuildEngine(), ProjectDirectory = _root, DevPackagePath = aggregate.OutputPath,
            DevPackageDiscoveryRoot = aggregate.TargetDirectory, PackageOutputPath = Path.Combine(aggregate.TargetDirectory, "platform.sunderpkg"),
            SourceFiles = generation.SourceFiles, ReferencePaths = generation.ReferencePaths, AuthoringReceipts = generation.AuthoringReceipts,
            IntermediateOutputPath = generation.IntermediateOutputPath, ContractCacheRoot = _cache, OutputType = "Exe",
            ProjectIdentity = generation.ProjectIdentity, Configuration = generation.Configuration, TargetFramework = generation.TargetFramework,
        };
        AssertSuccess(pack);
        var validated = await SunderPackageArchiveInspector.ExtractAndValidateAsync(pack.PackageOutputPath, Path.Combine(_root, "platform-inspected"));
        Assert.True(validated.Success, string.Join(Environment.NewLine, validated.Errors));
        Assert.Empty(validated.Manifest!.UsesContracts!); Assert.Empty(validated.Manifest.DependsOn!);
        Assert.Equal(before, File.ReadAllBytes(LockPath));
        var snapshot = Snapshot(generation.IntermediateOutputPath);
        AssertSuccess(CreateGeneration());
        Assert.Equal(snapshot, Snapshot(generation.IntermediateOutputPath));
    }

    [Theory]
    [InlineData("missing-catalog")]
    [InlineData("changed-catalog")]
    [InlineData("missing-handler")]
    [InlineData("broad-range")]
    public async Task PlatformGeneration_RejectsStaleEvidenceWithoutReplacingBindings(string failure)
    {
        var fixture = await PreparePlatformProviderAsync();
        var generation = CreateGeneration(); AssertSuccess(generation);
        var before = Snapshot(generation.IntermediateOutputPath);
        if (failure == "missing-catalog") File.Delete(SunderRpcPlatformCatalogCache.CatalogPath(_cache, fixture.Requirement.ContentIdentity));
        if (failure == "changed-catalog")
        {
            var document = JsonNode.Parse(fixture.Envelope)!; document["releaseVersion"] = "1.1.1";
            File.WriteAllBytes(SunderRpcPlatformCatalogCache.CatalogPath(_cache, fixture.Requirement.ContentIdentity), Compact(document));
        }
        if (failure == "missing-handler")
        {
            File.Delete(Path.Combine(_root, "Provider.cs")); File.WriteAllText(Path.Combine(_root, "Program.cs"), WorkerSource);
        }
        if (failure == "broad-range")
        {
            var value = ReadJson(LockPath); value["platformImports"]!["stackContributor"]!["authorityReleaseRange"] = ">=1.0.0 <2.0.0"; WriteJson(LockPath, value);
        }
        Assert.False(CreateGeneration().Execute());
        Assert.Equal(before, Snapshot(generation.IntermediateOutputPath));
    }

    private async Task<PlatformFixtureData> PreparePlatformProviderAsync()
    {
        var fixture = PlatformFixture();
        var value = ReadJson(LockPath); value["platformImports"]!["stackContributor"]!["authorityReleaseRange"] = ">=1.1.0 <1.2.0"; WriteJson(LockPath, value);
        File.Delete(Path.Combine(_root, "Consumer.cs"));
        File.WriteAllText(Path.Combine(_root, "Provider.cs"), PlatformProviderSource.Replace("IMinimalProviderService", "IStackContributorService", StringComparison.Ordinal)
            .Replace("CExample_Rpc_V1_Minimal", "CSunder_Stack_Contributor", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(_root, "Program.cs"), WorkerSource.Replace("new SunderWorkerOptions([])",
            "new SunderWorkerOptions([new LocalProvider().CreateSunderRpcProviderRegistration()])", StringComparison.Ordinal));
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException("Unexpected HTTP.")));
        await new SunderRpcContractRestore(http).RestoreAsync(LockPath, _cache, [], platformCatalog: fixture.Path);
        return fixture;
    }
}
