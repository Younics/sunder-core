using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed partial class SunderPackageArchiveInspectorTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("unindexed")]
    [InlineData("size")]
    [InlineData("hash")]
    [InlineData("misplaced")]
    [InlineData("role-shadow")]
    [InlineData("case-shadow")]
    [InlineData("private-entry")]
    [InlineData("descriptor-path")]
    [InlineData("policy-tamper")]
    public async Task ModelProjection_ArchiveBoundaryRejectsTamperingBeforePublicExport(string failure)
    {
        var root = CreateTempDirectory();
        try
        {
            var descriptor = CreateContractDescriptor("example.public", "1.0.0");
            var hidden = CreateContractDescriptor("example.private", "1.0.0");
            var manifest = CreateManifest([], [CreateContractBundle(descriptor,
                failure == "descriptor-path" ? SunderPackageFormat.RpcModelProjectionLogicalPath : "contracts/public.json"),
                CreateContractBundle(hidden, "contracts/private.json", "package")]);
            var bytes = ModelProjectionFixture.EmptyDocument(manifest);
            var files = new Dictionary<string, byte[]>
            {
                ["payload/shared/contracts/public.json"] = descriptor,
                ["payload/shared/contracts/private.json"] = hidden,
                [SunderPackageFormat.RpcModelProjectionPath] = bytes,
            };
            if (failure is "role-shadow" or "case-shadow")
                files[failure == "role-shadow" ? "payload/runtime/linux-x64/" + SunderPackageFormat.RpcModelProjectionLogicalPath
                    : "payload/app/shared/contracts/Rpc-model-projection.v1.json"] = bytes;
            if (failure == "private-entry")
            {
                var json = JsonNode.Parse(bytes)!;
                var entry = json["contracts"]![0]!.DeepClone();
                entry["contractId"] = "example.private";
                entry["descriptorSha256"] = DescriptorHash(hidden); entry["abiSha256"] = AbiHash(hidden);
                entry["modelProjection"]!["models"]!["Empty"] = "PrivateOnlyModel";
                entry["methodPolicies"] = JsonNode.Parse("""{"example":{"invoke":{"timeoutSeconds":2100}}}""");
                json["contracts"]!.AsArray().Insert(0, entry);
                files[SunderPackageFormat.RpcModelProjectionPath] = SunderRpcModelProjectionJson.Write(json);
            }
            var archive = CreatePackageArchive(root, manifest, files, index => index with
            {
                Files = index.Files!.Where(e => failure != "unindexed" || e.Path != SunderPackageFormat.RpcModelProjectionPath)
                    .Select(e => e.Path != SunderPackageFormat.RpcModelProjectionPath ? e : failure switch
                    {
                        "size" => e with { Size = e.Size + 1 }, "hash" => e with { Sha256 = new string('0', 64) }, _ => e,
                    }).ToArray(),
            });
            if (failure is "missing" or "misplaced")
            {
                using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
                zip.GetEntry(SunderPackageFormat.RpcModelProjectionPath)!.Delete();
                if (failure == "misplaced")
                { using var output = zip.CreateEntry(SunderPackageFormat.RpcModelProjectionFileName).Open(); output.Write(bytes); }
            }
            if (failure == "policy-tamper")
            {
                // Change only valid policy bytes, leaving descriptor/ABI and indexed pins fixed.
                using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
                zip.GetEntry(SunderPackageFormat.RpcModelProjectionPath)!.Delete();
                var changed = JsonNode.Parse(bytes)!;
                changed["contracts"]![0]!["methodPolicies"] = JsonNode.Parse("""{"example":{"invoke":{"timeoutSeconds":2100}}}""");
                using var output = zip.CreateEntry(SunderPackageFormat.RpcModelProjectionPath).Open();
                output.Write(SunderRpcModelProjectionJson.Write(changed));
            }
            var api = Path.Combine(root, "api");
            var result = await SunderPackagePublicApiInspection.ExportArchiveAsync(archive, api);
            Assert.False(result.Success);
            Assert.Contains(result.Errors, error => error.Contains("projection", StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(api));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("contracts/rpc-model-projection.v1.json")]
    [InlineData("Contracts/RPC-MODEL-PROJECTION.V1.JSON")]
    public void ModelProjection_LogicalNameIsReservedForManifestAssetReferences(string logicalPath)
    {
        var errors = new List<string>();
        Assert.False(PackageArchivePathValidator.TryParse(logicalPath, "asset", errors, out _));
        Assert.Contains("tool-reserved", Assert.Single(errors), StringComparison.Ordinal);
        errors.Clear();
        Assert.True(PackageArchivePathValidator.TryParse(SunderPackageFormat.RpcModelProjectionPath, "content-index path", errors,
            out _, maxLength: SunderPackageFormat.MaxArchivePathLength, maxDepth: SunderPackageFormat.MaxArchivePathDepth));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ModelProjection_PrivateOnlyArtifactExportsExplicitEmptyDocument()
    {
        var root = CreateTempDirectory();
        try
        {
            var descriptor = CreateContractDescriptor("example.private", "1.0.0");
            var manifest = CreateManifest([], [CreateContractBundle(descriptor, "contracts/private.json", "package")]);
            var archive = CreatePackageArchive(root, manifest, new Dictionary<string, byte[]>
            { ["payload/shared/contracts/private.json"] = descriptor });
            var output = Path.Combine(root, "api");
            var result = await SunderPackagePublicApiInspection.ExportArchiveAsync(archive, output);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(new[] { "inspection.json", SunderPackageFormat.RpcModelProjectionFileName },
                Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            var bytes = File.ReadAllBytes(Path.Combine(output, SunderPackageFormat.RpcModelProjectionFileName));
            Assert.Empty(SunderRpcModelProjectionDocument.Parse(bytes).Contracts);
            Assert.DoesNotContain("example.private", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ModelProjection_PublicInspectionMaximumInventoryHasExactly130Files()
    {
        var root = CreateTempDirectory();
        try
        {
            var descriptors = Enumerable.Range(0, 128).Select(i => CreateContractDescriptor($"example.contract{i:000}", "1.0.0")).ToArray();
            var bundles = descriptors.Select((d, i) => CreateContractBundle(d, $"contracts/{i:000}.json")).ToArray();
            var files = descriptors.Select((d, i) => (d, i)).ToDictionary(p => $"payload/shared/contracts/{p.i:000}.json", p => p.d);
            var archive = CreatePackageArchive(root, CreateManifest([], bundles), files);
            var output = Path.Combine(root, "api");
            var result = await SunderPackagePublicApiInspection.ExportArchiveAsync(archive, output);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(SunderPackagePublicApiInspection.MaximumOutputFiles, Directory.GetFiles(output).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
