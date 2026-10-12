using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Security.Cryptography;
using System.Xml.Linq;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcImportedBuildTests
{
    [Fact]
    public async Task OnlineRestoreReproducesTheLockAndPreservesCacheMtimesOnReplay()
    {
        _ = await CreateLockedCandidateAsync();
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        var artifact = contractLock.PackageArtifacts[DependencyId];
        var inventory = SunderRpcAuthoringDocuments.ValidatePackagePublicInventory(artifact);
        using var handler = new RestoreHandler(request => RestoreResponse(request, inventory));
        using var http = new HttpClient(handler);
        var root = Path.Combine(_root, "restored-cache");
        var before = File.ReadAllBytes(LockPath);
        var restore = new SunderRpcContractRestore(http);
        Assert.Equal(1, await restore.RestoreAsync(LockPath, root, []));
        Assert.Equal(_descriptor.GetCanonicalUtf8(), File.ReadAllBytes(SunderRpcContractCache.GetDescriptorPath(root, _descriptor.Sha256)));
        var timestamps = Directory.GetFiles(root).Select(path => (path, File.GetLastWriteTimeUtc(path))).ToArray();
        Assert.Equal(0, await restore.RestoreAsync(LockPath, root, []));
        Assert.All(timestamps, entry => Assert.Equal(entry.Item2, File.GetLastWriteTimeUtc(entry.path)));
        Assert.Equal(before, File.ReadAllBytes(LockPath));
        Assert.Single(SunderRpcContractCache.Resolve(contractLock, root).EntriesByDescriptorSha256);
        var generation = CreateGeneration();
        generation.ContractCacheRoot = root;
        AssertSuccess(generation);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(before, File.ReadAllBytes(LockPath));
    }

    [Theory]
    [InlineData("invalid-archive")]
    [InlineData("length")]
    [InlineData("media-type")]
    [InlineData("redirect")]
    [InlineData("changed-origin")]
    [InlineData("oversize")]
    public async Task RestoreRejectsUnverifiedResponsesWithoutPublishingCacheOrChangingTheLock(string failure)
    {
        _ = await CreateLockedCandidateAsync();
        var artifact = SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PackageArtifacts[DependencyId];
        var inventory = SunderRpcAuthoringDocuments.ValidatePackagePublicInventory(artifact);
        using var handler = new RestoreHandler(request =>
        {
            var response = RestoreResponse(request, inventory);
            Assert.EndsWith("/canonical", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            if (failure == "invalid-archive") response.Content = new ByteArrayContent([1, 2, 3]);
            if (failure == "media-type") response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (failure == "redirect") response.StatusCode = HttpStatusCode.Redirect;
            if (failure == "changed-origin") response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://other.example/canonical");
            if (failure == "oversize") response.Content.Headers.ContentLength = 536_870_913;
            if (failure == "length") response.Content.Headers.ContentLength = 1;
            return response;
        });
        using var http = new HttpClient(handler);
        var before = File.ReadAllBytes(LockPath);
        var root = Path.Combine(_root, "rejected-cache");
        await Assert.ThrowsAsync<InvalidDataException>(() => new SunderRpcContractRestore(http).RestoreAsync(LockPath, root, []));
        Assert.False(Directory.Exists(root));
        Assert.Equal(before, File.ReadAllBytes(LockPath));
    }

    private async Task<string> CreateLockedCandidateAsync()
    {
        var source = Path.Combine(_root, "candidate-source");
        Directory.CreateDirectory(Path.Combine(source, "manifest"));
        Directory.CreateDirectory(Path.Combine(source, "payload", "shared", "contracts"));
        File.WriteAllBytes(Path.Combine(source, "payload", "shared", "contracts", "api.json"), _descriptor.GetCanonicalUtf8());
        var manifest = new SunderPackageManifest
        {
            ArchiveFormatVersion = 1, ManifestVersion = 1, Id = DependencyId, Name = "Dependency", Version = "1.2.3", Targets = [], DependsOn = [],
            ContractBundles = [new() { Source = "owned", AuthorityKind = "package", AuthorityId = DependencyId, ContractId = ContractId, Version = _descriptor.Version,
                DescriptorPath = "contracts/api.json", DescriptorSha256 = _descriptor.Sha256, AbiSha256 = _descriptor.AbiSha256, Exposure = "public" }],
            UsesContracts = [], Provides = [], PackageArtifactImports = [], PackageApiImports = [], ProviderImports = [], PlatformImports = [],
        };
        File.WriteAllBytes(Path.Combine(source, SunderPackageFormat.ManifestPath), JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
        var projection = new SunderRpcModelProjectionDocument(1, manifest.Id!, manifest.Version!,
            [new(ContractId, _descriptor.Version, _descriptor.Sha256, _descriptor.AbiSha256, SunderRpcModelProjection.Empty)]);
        File.WriteAllBytes(Path.Combine(source, SunderPackageFormat.RpcModelProjectionPath), projection.ToCanonicalUtf8());
        var entries = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(path => new SunderPackageContentIndexEntry(
            Path.GetRelativePath(source, path).Replace('\\', '/'), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), new FileInfo(path).Length)).OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
        File.WriteAllBytes(Path.Combine(source, SunderPackageFormat.ContentIndexPath), JsonSerializer.SerializeToUtf8Bytes(new SunderPackageContentIndex(1, entries)));
        using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        contentHash.AppendData(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, SunderPackageFormat.ManifestPath))));
        contentHash.AppendData(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, SunderPackageFormat.ContentIndexPath))));
        var content = Convert.ToHexStringLower(contentHash.GetHashAndReset());
        var locked = ReadJson(LockPath);
        var artifact = locked["packageArtifacts"]![DependencyId]!;
        artifact["selectedPackageContentIdentity"] = content;
        artifact["contracts"]![ContractId]!["selectedAuthorityContentIdentity"] = content;
        var existing = SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PackageArtifacts[DependencyId];
        var inventory = JsonNode.Parse(SunderRpcAuthoringDocuments.ValidatePackagePublicInventory(existing).ToCanonicalUtf8())!;
        inventory["packageContentIdentity"] = content;
        inventory["contracts"]![0]!["selectedAuthorityContentIdentity"] = content;
        artifact["publicInventorySha256"] = SunderPackagePublicApiInventory.Parse(JsonSerializer.SerializeToUtf8Bytes(inventory,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })).PublicInventorySha256;
        artifact["modelProjectionBytes"] = projection.ModelProjectionBytes;
        artifact["modelProjectionSha256"] = projection.ModelProjectionSha256;
        WriteJson(LockPath, locked);
        var archive = Path.Combine(_root, "candidate.sunderpkg");
        await SunderArchive.WriteDeterministicAsync(source, archive);
        return archive;
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task OfflineCandidateHydratesTheSameDescriptorWithoutNetworkOrSelectionChanges()
    {
        var archive = await CreateLockedCandidateAsync();
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException("Offline restore cannot use HTTP.")));
        var before = File.ReadAllBytes(LockPath);
        var cache = Path.Combine(_root, "candidate-cache");
        Assert.Equal(1, await new SunderRpcContractRestore(http).RestoreAsync(LockPath, cache, [archive]));
        Assert.Equal(before, File.ReadAllBytes(LockPath));
        Assert.Equal(_descriptor.GetCanonicalUtf8(), File.ReadAllBytes(SunderRpcContractCache.GetDescriptorPath(cache, _descriptor.Sha256)));
        var project = Path.Combine(_root, "RestoreOnly.proj");
        var targets = Path.Combine(RepositoryRoot(), "languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Package.Build.targets");
        var taskCache = Path.Combine(_root, "msbuild-cache");
        new XDocument(new XElement("Project", new XElement("PropertyGroup",
            new XElement("Configuration", "Debug"), new XElement("TargetFramework", "net10.0"), new XElement("BaseIntermediateOutputPath", "restore-obj/"),
            new XElement("SunderPackageBuildTaskAssembly", typeof(RestoreSunderContractsTask).Assembly.Location),
            new XElement("SunderPackageBuildTaskSourceIsImmutable", "true"), new XElement("SunderPackageBuildProject", Path.Combine(_root, "no-source.csproj")),
            new XElement("SunderContractCacheRoot", taskCache), new XElement("SunderPackageCandidatePath", archive)),
            new XElement("Import", new XAttribute("Project", targets)))).Save(project);
        await RunDotnet("msbuild", project, "-t:RestoreSunderContracts", "-nr:false", "-nologo");
        Assert.Equal(before, File.ReadAllBytes(LockPath));
        Assert.Equal(_descriptor.GetCanonicalUtf8(), File.ReadAllBytes(SunderRpcContractCache.GetDescriptorPath(taskCache, _descriptor.Sha256)));
        Assert.Single(SunderRpcContractCache.Resolve(SunderRpcAuthoringDocuments.ReadContractLock(LockPath), taskCache).EntriesByDescriptorSha256);
        foreach (var option in new[] { "SunderPackageCandidatePath", "SunderRegistryOrigin", "SunderPlatformCatalogPath" })
        {
            var failure = await RunDotnet(false, "msbuild", project, "-t:RestoreSunderContracts", "-nr:false", "-nologo", "-p:" + option + "=");
            Assert.Contains(option == "SunderRegistryOrigin" ? "package-api.registry-origin-invalid"
                : option == "SunderPlatformCatalogPath" ? "platform-api.catalog-invalid" : "offline restore cannot fall back to network", failure, StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllBytes(LockPath));
        }
    }

    private HttpResponseMessage RestoreResponse(HttpRequestMessage request, SunderPackagePublicApiInventory inventory)
    {
        byte[] bytes;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        if (request.RequestUri!.AbsolutePath.EndsWith("/canonical", StringComparison.Ordinal))
        {
            bytes = File.ReadAllBytes(Path.Combine(_root, "candidate.sunderpkg"));
            response.Content = new ByteArrayContent(bytes);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return response;
        }
        if (request.RequestUri.AbsolutePath.EndsWith("/public-api", StringComparison.Ordinal))
            bytes = JsonSerializer.SerializeToUtf8Bytes(new { inventory = inventory.Payload, publicInventorySha256 = inventory.PublicInventorySha256 }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        else if (request.RequestUri.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal))
        {
            bytes = _descriptor.GetCanonicalUtf8();
            response.Headers.ETag = new EntityTagHeaderValue("\"" + _descriptor.Sha256 + "\"");
        }
        else bytes = JsonSerializer.SerializeToUtf8Bytes(new { contractId = _descriptor.ContractId, version = _descriptor.Version,
            descriptorSha256 = _descriptor.Sha256, abiSha256 = _descriptor.AbiSha256, exposure = "public", firstPublisherPackageId = DependencyId,
            size = _descriptor.GetCanonicalUtf8().Length, etag = "\"" + _descriptor.Sha256 + "\"" });
        response.Content = new ByteArrayContent(bytes);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(request.RequestUri.AbsolutePath.EndsWith("/descriptor", StringComparison.Ordinal)
            ? "application/schema+json" : "application/json");
        return response;
    }

    private sealed class RestoreHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; return Task.FromResult(response(request)); }
    }
}
