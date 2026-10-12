using System.Diagnostics;
using System.Net;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcImportedBuildTests
{
    [Fact]
    public async Task LocalRestore_HydratesPackageAndPlatformTogetherFromColdCacheWithoutRegistry()
    {
        var archive = await CreateLockedCandidateAsync();
        var locked = ReadJson(LockPath);
        foreach (var artifact in locked["packageArtifacts"]!.AsObject()) artifact.Value!.AsObject().Remove("registryOrigin");
        var platform = PlatformFixture();
        locked["platformImports"] = ReadJson(LockPath)["platformImports"]!.DeepClone();
        foreach (var import in locked["platformImports"]!.AsObject()) import.Value!.AsObject().Remove("registryOrigin");
        WriteJson(LockPath, locked);
        var before = File.ReadAllBytes(LockPath);
        using var handler = new RestoreHandler(_ => throw new InvalidOperationException("Local restore attempted HTTP."));
        using var http = new HttpClient(handler);
        var cache = Path.Combine(_root, "combined-local-cache");
        var incomplete = await Assert.ThrowsAsync<InvalidDataException>(() => new SunderRpcContractRestore(http)
            .RestoreAsync(LockPath, cache, [], platformCatalog: platform.Path));
        Assert.Contains("contract-cache.entry-missing", incomplete.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(cache)); Assert.Equal(before, File.ReadAllBytes(LockPath));
        Assert.Equal(2, await new SunderRpcContractRestore(http).RestoreAsync(LockPath, cache, [archive], platformCatalog: platform.Path));
        var selected = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        Assert.Equal(2, SunderRpcContractCache.Resolve(selected, cache).EntriesByDescriptorSha256.Count);
        Assert.Single(SunderRpcPlatformCatalogCache.Resolve(selected, cache));
        Assert.Equal(before, File.ReadAllBytes(LockPath)); Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task PlatformRestore_RegistryCatalogAndDescriptorsAreOfflineReplayableWithoutSigningInputs()
    {
        var fixture = PlatformFixture();
        using var handler = new RestoreHandler(_ => throw new InvalidOperationException("Offline restore must not use HTTP."));
        using var http = new HttpClient(handler);
        var restore = new SunderRpcContractRestore(http);
        var cache = Path.Combine(_root, "platform-cache");
        Assert.Equal(1, await restore.RestoreAsync(LockPath, cache, [], platformCatalog: fixture.Path));
        var catalog = Assert.Single(SunderRpcPlatformCatalogCache.Resolve(fixture.Lock, cache)).Value;
        Assert.Equal(fixture.Envelope, catalog.GetCanonicalUtf8());
        Assert.Single(SunderRpcContractCache.Resolve(fixture.Lock, cache).EntriesByDescriptorSha256);
        var files = Directory.GetFiles(cache, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.GetLastWriteTimeUtc);
        Assert.Equal(1, await restore.RestoreAsync(LockPath, cache, [], platformCatalog: fixture.Path));
        Assert.All(files, item => Assert.Equal(item.Value, File.GetLastWriteTimeUtc(item.Key)));
        Assert.Equal(fixture.Lock.ExactBytes, File.ReadAllBytes(LockPath));
        Assert.Equal(0, handler.Requests);

    }

    [Fact]
    public async Task PlatformRestore_OnlineBytesMatchOfflineAndMetadataOnlyConstraintsDoNotGrantBodies()
    {
        var fixture = PlatformFixture();
        using var handler = new RestoreHandler(request => PlatformResponse(request, fixture));
        using var http = new HttpClient(handler);
        var restore = new SunderRpcContractRestore(http);
        var online = Path.Combine(_root, "online-platform"); var offline = Path.Combine(_root, "offline-platform");
        Assert.Equal(1, await restore.RestoreAsync(LockPath, online, []));
        Assert.Equal(1, await restore.RestoreAsync(LockPath, offline, [], platformCatalog: fixture.Path));
        foreach (var file in Directory.GetFiles(online, "*", SearchOption.AllDirectories))
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(offline, Path.GetRelativePath(online, file))));
        Assert.Equal(2, handler.Requests);

        var mixed = ReadJson(Fixture("authoring/conformance/valid/mixed-contract-lock.json"));
        mixed["platformImports"] = new JsonObject(); WriteJson(LockPath, mixed);
        var metadataCache = Path.Combine(_root, "metadata-platform");
        var selected = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        SunderRpcContractCacheStore.Publish(metadataCache, new Dictionary<string, byte[]> { [_descriptor.Sha256] = _descriptor.GetCanonicalUtf8() },
            SunderRpcContractCache.GetRequirements(selected));
        foreach (var artifact in selected.PackageArtifacts.Values) ModelProjectionTestEvidence.Seed(metadataCache, artifact);
        Assert.Equal(1, await restore.RestoreAsync(LockPath, metadataCache, [], platformCatalog: fixture.Path));
        Assert.Single(SunderRpcPlatformCatalogCache.Resolve(SunderRpcAuthoringDocuments.ReadContractLock(LockPath), metadataCache));
        Assert.Equal(4, Directory.GetFiles(metadataCache, "*.json").Length);
        Assert.True(File.Exists(SunderRpcContractCache.GetDescriptorPath(metadataCache, fixture.Requirement.Descriptor.DescriptorSha256)));
        // Verifying the artifact's complete inventory does not materialize a
        // platform body or grant a provider/client use.
        Assert.DoesNotContain(fixture.Requirement.Descriptor.DescriptorSha256, SunderRpcContractCache.GetRequirements(selected).Keys);
        Assert.DoesNotContain(SunderRpcCSharpImportPrepass.Prepare(selected, metadataCache).Contracts,
            contract => contract.Descriptor.Sha256 == fixture.Requirement.Descriptor.DescriptorSha256);
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("inventory")]
    [InlineData("digest")]
    [InlineData("url")]
    [InlineData("etag")]
    [InlineData("length")]
    [InlineData("size-kind")]
    [InlineData("media")]
    [InlineData("redirect")]
    [InlineData("origin")]
    public async Task PlatformRestore_RejectsUnverifiedResponsesBeforePublishing(string failure)
    {
        var fixture = PlatformFixture();
        using var handler = new RestoreHandler(request =>
        {
            var response = PlatformResponse(request, fixture);
            if (request.RequestUri!.AbsolutePath.EndsWith("/catalog", StringComparison.Ordinal))
            {
                if (failure == "content")
                {
                    var value = JsonNode.Parse(fixture.Envelope)!;
                    value["releaseVersion"] = "1.1.1";
                    response.Content = new ByteArrayContent(Compact(value)); response.Content.Headers.ContentType = new("application/json");
                }
                if (failure == "etag") response.Headers.ETag = new EntityTagHeaderValue("\"" + fixture.Requirement.ContentIdentity + "\"", true);
                if (failure == "length") response.Content.Headers.ContentLength = fixture.Envelope.Length - 1;
                if (failure == "media") response.Content.Headers.ContentType = new("application/schema+json");
                if (failure == "redirect") response.StatusCode = HttpStatusCode.Redirect;
                if (failure == "origin") response.RequestMessage = new(HttpMethod.Get, "https://other.example/catalog");
            }
            else
            {
                var value = JsonNode.Parse(response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult())!;
                if (failure == "inventory") value["contracts"] = new JsonArray();
                if (failure == "digest") value["catalogSha256"] = new string('f', 64);
                if (failure == "url") value["catalogDownloadUrl"] = "https://other.example/catalog";
                if (failure == "size-kind") value["catalogBytes"] = "not-a-number";
                response.Content = new ByteArrayContent(Compact(value)); response.Content.Headers.ContentType = new("application/json");
            }
            return response;
        });
        using var http = new HttpClient(handler);
        var root = Path.Combine(_root, "rejected-platform");
        await Assert.ThrowsAsync<InvalidDataException>(() => new SunderRpcContractRestore(http).RestoreAsync(LockPath, root, []));
        Assert.False(Directory.Exists(root)); Assert.Equal(fixture.Lock.ExactBytes, File.ReadAllBytes(LockPath));
    }

    [Fact]
    public async Task PlatformRestore_ChangedLockCannotAdoptInputAuthority()
    {
        var fixture = PlatformFixture();
        using var handler = new RestoreHandler(request =>
        {
            var response = PlatformResponse(request, fixture);
            if (request.RequestUri!.AbsolutePath.EndsWith("/catalog", StringComparison.Ordinal)) File.WriteAllText(LockPath, "{}\n");
            return response;
        });
        using var http = new HttpClient(handler);
        var root = Path.Combine(_root, "changed-platform-lock");
        await Assert.ThrowsAsync<InvalidDataException>(() => new SunderRpcContractRestore(http).RestoreAsync(LockPath, root, []));
        Assert.Equal(2, handler.Requests);
        Assert.Equal("{}\n", File.ReadAllText(LockPath)); Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task PlatformRestore_InterruptedCatalogRepairPreservesCorruptionAndExplicitReplayCompletes()
    {
        var fixture = PlatformFixture();
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException()));
        var restore = new SunderRpcContractRestore(http); var root = Path.Combine(_root, "repair-platform");
        await restore.RestoreAsync(LockPath, root, [], platformCatalog: fixture.Path);
        var target = SunderRpcPlatformCatalogCache.CatalogPath(root, fixture.Requirement.ContentIdentity);
        File.WriteAllText(target, "corrupt evidence");
        Assert.Throws<InvalidDataException>(() => SunderRpcPlatformCatalogCache.Resolve(fixture.Lock, root));
        Assert.Throws<IOException>(() => SunderRpcPlatformCatalogCache.Publish(root,
            new Dictionary<string, byte[]> { [fixture.Requirement.ContentIdentity] = fixture.Envelope },
            SunderRpcPlatformCatalogCache.Requirements(fixture.Lock),
            checkpoint: point => { if (point == "catalog-quarantined") throw new IOException("interrupted"); }));
        Assert.False(File.Exists(target));
        var repair = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(target)!, ".repair-*"));
        Assert.Equal("corrupt evidence", File.ReadAllText(Path.Combine(repair, Path.GetFileName(target))));
        await restore.RestoreAsync(LockPath, root, [], platformCatalog: fixture.Path);
        Assert.Equal(fixture.Envelope, File.ReadAllBytes(target));
        Assert.Single(SunderRpcPlatformCatalogCache.Resolve(fixture.Lock, root));
    }

    [Fact]
    public async Task PlatformRestore_GcCollectsUnreferencedOwnedCatalogsAndDescriptors()
    {
        var fixture = PlatformFixture();
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException()));
        var cache = Path.Combine(_root, "platform-gc");
        await new SunderRpcContractRestore(http).RestoreAsync(LockPath, cache, [], platformCatalog: fixture.Path);
        foreach (var path in Directory.GetFiles(cache, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        File.Copy(Fixture("authoring/conformance/valid/minimal-contract-lock.json"), LockPath, overwrite: true);
        var result = SunderRpcContractCacheStore.Collect(cache, [LockPath]);
        Assert.Equal(2, result.RemovedEntries); Assert.Equal(0, result.RetainedUnprovenArtifacts);
        Assert.Empty(Directory.GetFiles(Path.Combine(cache, SunderRpcPlatformCatalogCache.DirectoryName)));
    }

    [ExternalArtifactFact("SUNDER_REQUIRE_NODE_OUTPUT_LOCK_INTEROP")]
    [Trait("Category", "ExternalArtifact")]
    public async Task PlatformRestore_NodeAndCSharpShareCatalogAndDescriptorBytes()
    {
        var fixture = PlatformFixture();
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException()));
        var root = Path.Combine(_root, "managed-platform-cache");
        await new SunderRpcContractRestore(http).RestoreAsync(LockPath, root, [], platformCatalog: fixture.Path);
        var nodeRoot = Path.Combine(_root, "node-platform-cache");
        var helper = Path.Combine(RepositoryRoot(), "languages/typescript/node/packages/package-tool/dist-test/test/platform-restore-child.js");
        var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { helper, _root, nodeRoot, fixture.Path, root }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); Assert.True(process.ExitCode == 0, await stdout + await stderr); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.Single(SunderRpcPlatformCatalogCache.Resolve(fixture.Lock, nodeRoot));
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(nodeRoot, Path.GetRelativePath(root, file))));
    }

    private PlatformFixtureData PlatformFixture()
    {
        var locked = ReadJson(Fixture("authoring/conformance/valid/mixed-contract-lock.json"));
        locked["packageArtifacts"] = new JsonObject(); locked["packageImports"] = new JsonObject();
        WriteJson(LockPath, locked);
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        var envelope = Compact(ReadJson(Fixture("authoring/conformance/valid/platform-catalog.json")));
        var path = Path.Combine(_root, "registry-catalog.json"); File.WriteAllBytes(path, envelope);
        return new(contractLock, Assert.Single(SunderRpcPlatformCatalogCache.Requirements(contractLock)), envelope, path);
    }
    private static byte[] Compact(JsonNode value) => SunderRpcAuthoringJson.WriteCompact(JsonSerializer.SerializeToElement(value), SunderRpcPlatformCatalogCache.MaximumBytes);
    private static HttpResponseMessage PlatformResponse(HttpRequestMessage request, PlatformFixtureData fixture)
    {
        var catalog = SunderPlatformContractCatalog.Verify(fixture.Envelope);
        var bytes = request.RequestUri!.AbsolutePath.EndsWith("/catalog", StringComparison.Ordinal) ? fixture.Envelope : JsonSerializer.SerializeToUtf8Bytes(new
        {
            authorityId = "sunder.core", releaseVersion = catalog.ReleaseVersion, catalogSha256 = catalog.ContentIdentity, catalogBytes = fixture.Envelope.Length,
            catalogDownloadUrl = new Uri(new Uri(fixture.Requirement.RegistryOrigin!), "api/v1/platform-contracts/sunder.core/releases/" + Uri.EscapeDataString(catalog.ReleaseVersion) + "/catalog").AbsoluteUri,
            lifecycleState = "Supported", publishedAtUtc = "2026-09-16T00:00:00Z", contracts = catalog.Contracts.Select(descriptor => new
            { contractId = descriptor.ContractId, contractVersion = descriptor.Version, descriptorBytes = descriptor.GetCanonicalUtf8().Length, descriptorSha256 = descriptor.Sha256, abiSha256 = descriptor.AbiSha256 }),
        });
        var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("application/json");
        if (request.RequestUri.AbsolutePath.EndsWith("/catalog", StringComparison.Ordinal)) response.Headers.ETag = new("\"" + catalog.ContentIdentity + "\"");
        return response;
    }
    private sealed record PlatformFixtureData(SunderRpcContractLock Lock, SunderRpcPlatformRequirement Requirement, byte[] Envelope, string Path);
}
