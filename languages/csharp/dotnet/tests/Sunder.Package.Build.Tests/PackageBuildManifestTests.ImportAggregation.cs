using System.Text.Json.Nodes;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class PackageBuildManifestTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AggregateRoleImportsCoalesceExactEvidenceAndRequiredProviderPresence(bool provider)
    {
        var root = CreateTempDirectory();
        var runtime = await ImportedRoleLeaf(root, "runtime", "linux-x64", provider);
        var app = await ImportedRoleLeaf(root, "app", "linux-x64", false);
        var task = ImportAggregate(root, runtime, app); Assert.True(task.Execute(), string.Join(" | ", ((TestBuildEngine)task.BuildEngine).Errors));
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(task.OutputPath);
        Assert.True(validation.Success, string.Join(" | ", validation.Errors)); var manifest = validation.Manifest!;
        Assert.Single(manifest.PackageArtifactImports!); Assert.Single(manifest.ContractBundles!);
        var api = Assert.Single(manifest.PackageApiImports!)!; Assert.Equal("optional", api.Presence);
        Assert.Equal(["app", "runtime"], api.UseRefs!.Select(value => value!.Role));
        Assert.Equal(2, manifest.UsesContracts!.Count);
        Assert.Equal(provider ? "required" : "optional", Assert.Single(manifest.DependsOn!).Presence);
        Assert.Equal(provider ? 1 : 0, manifest.ProviderImports!.Count);
        var appOnly = ImportAggregate(Path.Combine(root, "app-only"), app);
        Assert.True(appOnly.Execute(), string.Join(" | ", ((TestBuildEngine)appOnly.BuildEngine).Errors));
        var projected = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(appOnly.OutputPath);
        Assert.True(projected.Success); Assert.Equal("app", Assert.Single(Assert.Single(projected.Manifest!.PackageApiImports!)!.UseRefs!)!.Role);
    }

    [Theory]
    [InlineData("presence")]
    [InlineData("range")]
    [InlineData("authority")]
    public async Task AggregateRoleImportsRejectConflictingReviewBeforeReplacingOutput(string conflict)
    {
        var root = CreateTempDirectory(); var runtime = await ImportedRoleLeaf(root, "runtime", "linux-x64", false);
        var app = await ImportedRoleLeaf(root, "app", "linux-x64", false); var task = ImportAggregate(root, runtime, app);
        Assert.True(task.Execute(), string.Join(" | ", ((TestBuildEngine)task.BuildEngine).Errors));
        var path = Path.Combine(task.OutputPath, SunderPackageFormat.ManifestPath); var original = File.ReadAllBytes(path);
        var appManifestPath = Path.Combine(app, SunderPackageFormat.ManifestPath); var json = JsonNode.Parse(File.ReadAllBytes(appManifestPath))!;
        if (conflict == "presence") { json["dependsOn"]![0]!["presence"] = "required"; json["packageApiImports"]![0]!["presence"] = "required"; }
        if (conflict == "range") { json["dependsOn"]![0]!["versionRange"] = ">=1.2.3 <3.0.0"; json["packageArtifactImports"]![0]!["requestedVersionRange"] = ">=1.2.3 <3.0.0"; }
        if (conflict == "authority")
        {
            json["packageArtifactImports"]![0]!["selectedPackageVersion"] = "1.2.4";
            json["packageArtifactImports"]![0]!["authorityConstraints"]![0]!["selectedAuthorityRelease"] = "1.2.4";
            json["contractBundles"]![0]!["selectedAuthorityRelease"] = "1.2.4";
        }
        File.WriteAllText(appManifestPath, json.ToJsonString()); PackageContentIndexer.Write(app);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(app);
        Assert.True(validation.Success, string.Join(" | ", validation.Errors));
        task.BuildEngine = new TestBuildEngine(); Assert.False(task.Execute()); Assert.Equal(original, File.ReadAllBytes(path));
    }

    private static AggregateSunderPackageTask ImportAggregate(string root, params string[] leaves)
    {
        var project = Path.Combine(root, "aggregate"); var target = Path.Combine(project, "bin");
        return new() { BuildEngine = new TestBuildEngine(), TargetLeaves = leaves.Select(path => TargetLeafItem(path)).ToArray(),
            OutputPath = Path.Combine(target, "sunder-dev"), OutputDiscoveryRoot = target, ProjectDirectory = project, TargetDirectory = target, ExpectedPackageVersion = "1.2.3" };
    }

    private static async Task<string> ImportedRoleLeaf(string root, string role, string rid, bool provider)
    {
        var leaf = CreateRpcLeaf(root, role, rid, required: false, audience: "public", includeProvider: provider);
        var path = Path.Combine(leaf, SunderPackageFormat.ManifestPath); var manifest = JsonNode.Parse(File.ReadAllBytes(path))!;
        var bundle = manifest["contractBundles"]![0]!; var id = bundle["contractId"]!.GetValue<string>(); var version = bundle["version"]!.GetValue<string>();
        var authority = new JsonObject { ["authorityKind"] = "package", ["authorityId"] = "test.dependency", ["selectedAuthorityRelease"] = "1.2.3", ["selectedAuthorityContentIdentity"] = new string('1', 64) };
        bundle["source"] = "imported"; foreach (var item in authority) bundle[item.Key] = item.Value!.DeepClone();
        manifest["dependsOn"] = new JsonArray(new JsonObject { ["packageId"] = "test.dependency", ["versionRange"] = ">=1.2.3 <2.0.0", ["presence"] = provider ? "required" : "optional" });
        manifest["packageArtifactImports"] = new JsonArray(new JsonObject
        {
            ["packageId"] = "test.dependency", ["requestedVersionRange"] = ">=1.2.3 <2.0.0", ["includePrerelease"] = false, ["selectedPackageVersion"] = "1.2.3",
            ["selectedPackageContentIdentity"] = new string('1', 64), ["publicContractCount"] = 1, ["publicInventorySha256"] = new string('3', 64),
            ["authorityConstraints"] = new JsonArray(authority.DeepClone()),
        });
        var reference = new JsonObject { ["contractId"] = id, ["contractVersion"] = version };
        manifest["packageApiImports"] = new JsonArray(new JsonObject { ["packageId"] = "test.dependency", ["presence"] = "optional", ["bundleRefs"] = new JsonArray(reference.DeepClone()),
            ["useRefs"] = new JsonArray(new JsonObject { ["role"] = role, ["contractId"] = id, ["contractVersion"] = version }) });
        if (provider) manifest["providerImports"] = new JsonArray(new JsonObject { ["key"] = "implementation", ["packageId"] = "test.dependency", ["contractId"] = id, ["contractVersion"] = version,
            ["bundleRef"] = reference.DeepClone(), ["providerRefs"] = new JsonArray(new JsonObject { ["role"] = role, ["providerId"] = manifest["provides"]![0]!["providerId"]!.GetValue<string>(),
                ["contractId"] = id, ["contractVersion"] = version }) });
        File.WriteAllText(path, manifest.ToJsonString()); PackageContentIndexer.Write(leaf);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(leaf);
        Assert.True(validation.Success, string.Join(" | ", validation.Errors)); return leaf;
    }
}
