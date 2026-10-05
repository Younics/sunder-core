using System.Diagnostics;
using System.Text.Json.Nodes;
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
    public void PlatformCacheGc_ProtectsDirectAndMetadataReferencesAndYoungPairs(bool metadataOnly)
    {
        var fixture = PlatformFixture(); var cache = Path.Combine(_root, "catalog-gc");
        PublishGcCatalog(cache, fixture); AgeCatalogTree(cache);
        if (metadataOnly) SelectMetadataCatalog();
        var source = File.ReadAllBytes(LockPath); var sourceTime = File.GetLastWriteTimeUtc(LockPath);
        var body = SunderRpcPlatformCatalogCache.CatalogPath(cache, fixture.Requirement.ContentIdentity);
        var bodyTime = File.GetLastWriteTimeUtc(body);
        Assert.Equal(new(0, 0, 0, false), SunderRpcContractCacheStore.Collect(cache, [LockPath, LockPath]));
        Assert.Equal(source, File.ReadAllBytes(LockPath)); Assert.Equal(sourceTime, File.GetLastWriteTimeUtc(LockPath));
        Assert.Equal(bodyTime, File.GetLastWriteTimeUtc(body));

        ClearCatalogGcLock(); File.SetLastWriteTimeUtc(body, DateTime.UtcNow);
        Assert.Equal(0, SunderRpcContractCacheStore.Collect(cache, [LockPath]).RemovedEntries);
        AgeCatalogTree(cache);
        Assert.Equal(new(1, 0, 0, false), SunderRpcContractCacheStore.Collect(cache, [LockPath]));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(body)!));
    }

    [Theory]
    [InlineData("gc-prepared")]
    [InlineData("gc-tombstoned")]
    [InlineData("gc-moved")]
    [InlineData("gc-committed")]
    [InlineData("gc-deleted")]
    public void PlatformCacheGc_RecoversEveryCollectionBoundary(string boundary)
    {
        var fixture = PlatformFixture(); var cache = Path.Combine(_root, "catalog-gc");
        PublishGcCatalog(cache, fixture); AgeCatalogTree(cache); ClearCatalogGcLock();
        Assert.Throws<IOException>(() => SunderRpcContractCacheStore.Collect(cache, [LockPath], checkpoint: point =>
        { if (point == boundary || point.StartsWith(boundary + ":", StringComparison.Ordinal)) throw new IOException("interrupted"); }));
        if (boundary is not ("gc-committed" or "gc-deleted"))
        {
            Assert.Contains("collection is incomplete", Assert.Throws<InvalidDataException>(() => SunderRpcPlatformCatalogCache.Resolve(fixture.Lock, cache)).Message);
            Assert.Throws<InvalidDataException>(() => PublishGcCatalog(cache, fixture));
        }
        SunderRpcContractCacheStore.Collect(cache, [LockPath]);
        Assert.Empty(Directory.GetFiles(Path.Combine(cache, SunderRpcPlatformCatalogCache.DirectoryName)));
        Assert.Empty(Directory.GetDirectories(cache, ".gc-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlatformCacheGc_CommitFenceAndNewMetadataMarksRestoreTombstones(bool editAtFence)
    {
        var fixture = PlatformFixture(); var cache = Path.Combine(_root, "catalog-gc");
        PublishGcCatalog(cache, fixture); AgeCatalogTree(cache); ClearCatalogGcLock();
        Assert.NotNull(Record.Exception(() => SunderRpcContractCacheStore.Collect(cache, [LockPath], checkpoint: point =>
        {
            if (point != "gc-moved") return;
            if (editAtFence) SelectMetadataCatalog(); else throw new IOException("interrupted");
        })));
        SelectMetadataCatalog(); var source = File.ReadAllBytes(LockPath);
        Assert.Equal(0, SunderRpcContractCacheStore.Collect(cache, [LockPath]).RemovedEntries);
        var locked = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        Assert.Equal(fixture.Envelope, Assert.Single(SunderRpcPlatformCatalogCache.Resolve(locked, cache)).Value.GetCanonicalUtf8());
        Assert.Equal(source, File.ReadAllBytes(LockPath));
    }

    [Theory]
    [InlineData("catalog-staged")]
    [InlineData("catalog-quarantined")]
    [InlineData("catalog-published")]
    [InlineData("catalog-committed")]
    public void PlatformCacheGc_CollectsProvenInterruptedPublicationAndRepairArtifacts(string boundary)
    {
        var fixture = PlatformFixture(); var cache = Path.Combine(_root, "catalog-gc");
        PublishGcCatalog(cache, fixture);
        var body = SunderRpcPlatformCatalogCache.CatalogPath(cache, fixture.Requirement.ContentIdentity);
        // Repair preimages may exceed the descriptor limit; the catalog limit applies.
        File.WriteAllBytes(body, new byte[4_194_305]);
        File.WriteAllText(Path.ChangeExtension(body, ".receipt.json"), "retained broken receipt");
        Assert.Throws<IOException>(() => PublishGcCatalog(cache, fixture, point => { if (point == boundary) throw new IOException("interrupted"); }));
        PublishGcCatalog(cache, fixture);
        var artifacts = Directory.GetDirectories(Path.GetDirectoryName(body)!);
        Assert.NotEmpty(artifacts); AgeCatalogTree(cache);
        var result = SunderRpcContractCacheStore.Collect(cache, [LockPath]);
        Assert.Equal(0, result.RemovedEntries); Assert.Equal(artifacts.Length, result.RemovedArtifacts); Assert.Equal(0, result.RetainedUnprovenArtifacts);
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(body)!));
        Assert.Single(SunderRpcPlatformCatalogCache.Resolve(fixture.Lock, cache));
    }

    [Theory]
    [InlineData("body")]
    [InlineData("receipt")]
    [InlineData("missing-receipt")]
    [InlineData("orphan-receipt")]
    [InlineData("legacy-corrupt")]
    [InlineData("stage-extra")]
    [InlineData("stage-intent")]
    [InlineData("stage-receipt")]
    [InlineData("repair-preimage")]
    public void PlatformCacheGc_PreservesUnprovenCatalogBytes(string failure)
    {
        var fixture = PlatformFixture(); var cache = Path.Combine(_root, "catalog-gc");
        PublishGcCatalog(cache, fixture);
        var body = SunderRpcPlatformCatalogCache.CatalogPath(cache, fixture.Requirement.ContentIdentity);
        var directory = Path.GetDirectoryName(body)!; var receipt = Path.ChangeExtension(body, ".receipt.json");
        var protectedPath = failure is "receipt" or "orphan-receipt" ? receipt : body;
        if (failure == "body") File.WriteAllText(body, "changed");
        if (failure == "receipt") File.WriteAllText(receipt, "{}");
        if (failure == "missing-receipt") File.Delete(receipt);
        if (failure == "orphan-receipt") File.Delete(body);
        if (failure == "legacy-corrupt") { protectedPath = Path.Combine(directory, ".corrupt-legacy"); File.WriteAllText(protectedPath, "unproven preimage"); }
        if (failure.StartsWith("stage-", StringComparison.Ordinal) || failure == "repair-preimage")
        {
            File.WriteAllText(body, "broken");
            Assert.Throws<IOException>(() => PublishGcCatalog(cache, fixture, point => { if (point == "catalog-staged") throw new IOException("interrupted"); }));
            PublishGcCatalog(cache, fixture);
            var stage = Assert.Single(Directory.GetDirectories(directory, ".stage-*"));
            if (failure == "stage-extra") { protectedPath = Path.Combine(stage, "user.txt"); File.WriteAllText(protectedPath, "keep"); }
            if (failure == "stage-intent") { protectedPath = Path.Combine(stage, "intent.json"); File.WriteAllText(protectedPath, "{}"); }
            if (failure == "stage-receipt") { protectedPath = Path.Combine(stage, Path.GetFileName(receipt)); File.WriteAllBytes(protectedPath, SunderRpcPlatformCatalogCache.ReceiptBytes(fixture.Requirement.ContentIdentity, fixture.Envelope.Length + 1)); }
            if (failure == "repair-preimage") { protectedPath = Path.Combine(Assert.Single(Directory.GetDirectories(directory, ".repair-*")), Path.GetFileName(body)); File.WriteAllText(protectedPath, "unexpected user data"); }
        }
        AgeCatalogTree(cache);
        if (failure is "body" or "receipt" or "missing-receipt" or "orphan-receipt") ClearCatalogGcLock();
        var retained = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var result = SunderRpcContractCacheStore.Collect(cache, [LockPath]);
        Assert.Equal(0, result.RemovedEntries); Assert.Equal(1, result.RetainedUnprovenArtifacts);
        foreach (var (path, bytes) in retained.Where(pair => File.Exists(pair.Key))) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(retained[protectedPath], File.ReadAllBytes(protectedPath));
        if (failure.StartsWith("stage-", StringComparison.Ordinal)) Assert.Single(Directory.GetDirectories(directory, ".stage-*"));
        if (failure == "repair-preimage") Assert.Single(Directory.GetDirectories(directory, ".repair-*"));
    }

    [Theory]
    [InlineData("linked-body")]
    [InlineData("linked-repair")]
    [InlineData("changed-tombstone")]
    [InlineData("intent-traversal")]
    [InlineData("intent-size")]
    [InlineData("intent-batch-size")]
    public void PlatformCacheGc_RejectsUnsafePathsAndChangedRecoveryPreimages(string failure)
    {
        var fixture = PlatformFixture(); var cache = Path.Combine(_root, "catalog-gc");
        PublishGcCatalog(cache, fixture);
        var body = SunderRpcPlatformCatalogCache.CatalogPath(cache, fixture.Requirement.ContentIdentity);
        var outside = Path.Combine(_root, "outside"); File.WriteAllText(outside, "must survive");
        if (failure == "linked-body") { File.Delete(body); File.CreateSymbolicLink(body, outside); }
        if (failure == "linked-repair")
        {
            File.WriteAllText(body, "broken"); PublishGcCatalog(cache, fixture);
            var repairBody = Path.Combine(Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(body)!, ".repair-*")), Path.GetFileName(body));
            File.Delete(repairBody); File.CreateSymbolicLink(repairBody, outside);
        }
        if (!failure.StartsWith("linked-", StringComparison.Ordinal))
        {
            AgeCatalogTree(cache); ClearCatalogGcLock();
            Assert.Throws<IOException>(() => SunderRpcContractCacheStore.Collect(cache, [LockPath], checkpoint: point =>
            { if (point == "gc-moved") throw new IOException("interrupted"); }));
            var pending = Path.Combine(cache, ".gc-pending");
            if (failure == "changed-tombstone") File.WriteAllText(Path.Combine(pending, "0000", Path.GetFileName(body)), "changed tombstone");
            else
            {
                var path = Path.Combine(pending, "intent.json"); var intent = ReadJson(path);
                if (failure == "intent-traversal") intent["units"]![0]!["key"] = "platform-catalogs/../outside";
                else if (failure == "intent-size") intent["units"]![0]!["files"]![0]!["bytes"] = SunderRpcPlatformCatalogCache.MaximumBytes + 1;
                else
                {
                    var units = new JsonArray();
                    for (var index = 0; index < 33; index++)
                    {
                        var hash = index.ToString("x64"); var unit = intent["units"]![0]!.DeepClone();
                        unit["key"] = "platform-catalogs/" + hash;
                        unit["files"]![0]!["name"] = hash + ".json"; unit["files"]![0]!["sha256"] = hash; unit["files"]![0]!["bytes"] = 67_108_864;
                        unit["files"]![1]!["name"] = hash + ".receipt.json"; units.Add(unit);
                    }
                    intent["units"] = units;
                }
                WriteJson(path, intent);
            }
        }
        var error = Record.Exception(() => SunderRpcContractCacheStore.Collect(cache, [LockPath])); Assert.NotNull(error);
        if (failure == "intent-batch-size") Assert.Contains("GC file shape is invalid", error.Message);
        Assert.Equal("must survive", File.ReadAllText(outside));
        if (failure == "changed-tombstone") Assert.Equal("changed tombstone", File.ReadAllText(Path.Combine(cache, ".gc-pending/0000", Path.GetFileName(body))));
    }

    [Fact]
    public void PlatformCacheGc_PreservesChangedCompletionPreimages()
    {
        var fixture = PlatformFixture(); var cache = Path.Combine(_root, "catalog-gc");
        PublishGcCatalog(cache, fixture); var body = SunderRpcPlatformCatalogCache.CatalogPath(cache, fixture.Requirement.ContentIdentity);
        File.Delete(Path.ChangeExtension(body, ".receipt.json")); var changed = "";
        Assert.Contains("cleanup preimage changed", Assert.Throws<InvalidDataException>(() => PublishGcCatalog(cache, fixture, point =>
        {
            if (point != "catalog-committed") return;
            changed = Path.Combine(Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(body)!, ".completed-*")), Path.GetFileName(body));
            File.WriteAllText(changed, "user replacement");
        })).Message);
        AgeCatalogTree(cache); Assert.Equal(1, SunderRpcContractCacheStore.Collect(cache, [LockPath]).RetainedUnprovenArtifacts);
        Assert.Equal("user replacement", File.ReadAllText(changed)); Assert.Single(SunderRpcPlatformCatalogCache.Resolve(fixture.Lock, cache));
    }

    [ExternalArtifactFact("SUNDER_REQUIRE_NODE_OUTPUT_LOCK_INTEROP")]
    [Trait("Category", "ExternalArtifact")]
    public async Task PlatformCacheGc_CrossLanguageCrashRecoveryPublicationAndLeaseContention()
    {
        var fixture = PlatformFixture(); var cache = Path.Combine(_root, "catalog-gc");
        var retainedLock = Path.Combine(_root, "retained-lock.json"); File.WriteAllBytes(retainedLock, fixture.Lock.ExactBytes);
        PublishGcCatalog(cache, fixture); AgeCatalogTree(cache); ClearCatalogGcLock();
        Assert.Equal(86, await Node("gc-crash"));
        Assert.Equal(1, SunderRpcContractCacheStore.Collect(cache, [LockPath]).RemovedEntries);

        PublishGcCatalog(cache, fixture); AgeCatalogTree(cache);
        Assert.Throws<IOException>(() => SunderRpcContractCacheStore.Collect(cache, [LockPath], checkpoint: point => { if (point == "gc-moved") throw new IOException("interrupted"); }));
        SelectMetadataCatalog(); Assert.Equal(0, await Node("gc")); Assert.Equal(0, await Node("catalog-read"));
        Assert.Single(SunderRpcPlatformCatalogCache.Resolve(fixture.Lock, cache));

        foreach (var boundary in new[] { "catalog-staged", "catalog-quarantined", "catalog-published", "catalog-committed" })
        {
            var body = SunderRpcPlatformCatalogCache.CatalogPath(cache, fixture.Requirement.ContentIdentity);
            File.WriteAllText(body, "Node repair evidence");
            Assert.Equal(86, await Node("catalog-crash", boundary)); PublishGcCatalog(cache, fixture);
            AgeCatalogTree(cache); Assert.True(SunderRpcContractCacheStore.Collect(cache, [LockPath]).RemovedArtifacts > 0);
            File.WriteAllText(body, "C# repair evidence");
            Assert.Throws<IOException>(() => PublishGcCatalog(cache, fixture, point => { if (point == boundary) throw new IOException("interrupted"); }));
            Assert.Equal(0, await Node("catalog-publish")); AgeCatalogTree(cache); Assert.Equal(0, await Node("gc"));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(body)!));
            Assert.Single(SunderRpcPlatformCatalogCache.Resolve(fixture.Lock, cache));
        }

        using (var held = GeneratedOutputLock.Acquire([cache]))
        using (var waiting = Start("gc"))
        {
            try
            {
                Assert.Equal("collecting", await waiting.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                await Task.Delay(200); Assert.False(waiting.HasExited); held.Dispose();
                await waiting.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(waiting.ExitCode == 0, await waiting.StandardError.ReadToEndAsync());
            }
            finally { if (!waiting.HasExited) waiting.Kill(entireProcessTree: true); }
        }
        ClearCatalogGcLock(); AgeCatalogTree(cache);
        using var prepared = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var collecting = Task.Run(() => SunderRpcContractCacheStore.Collect(cache, [LockPath], checkpoint: point =>
        { if (point == "gc-prepared") { prepared.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(20))); } }));
        Assert.True(prepared.Wait(TimeSpan.FromSeconds(10)));
        using (var restoring = Start("catalog-publish", useRetainedLock: true))
        {
            try
            {
                Assert.Equal("publishing", await restoring.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                await Task.Delay(200); Assert.False(restoring.HasExited); release.Set();
                Assert.Equal(1, (await collecting.WaitAsync(TimeSpan.FromSeconds(10))).RemovedEntries);
                await restoring.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(restoring.ExitCode == 0, await restoring.StandardError.ReadToEndAsync());
            }
            finally { release.Set(); if (!restoring.HasExited) restoring.Kill(entireProcessTree: true); }
        }
        Assert.Single(SunderRpcPlatformCatalogCache.Resolve(fixture.Lock, cache));

        Process Start(string mode, string boundary = "", bool useRetainedLock = false)
        {
            var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { Path.Combine(RepositoryRoot(), "languages/typescript/node/packages/package-tool/dist-test/test/contract-cache-child.js"),
                mode, useRetainedLock ? retainedLock : LockPath, cache, fixture.Path, boundary }) start.ArgumentList.Add(arg);
            return Process.Start(start)!;
        }
        async Task<int> Node(string mode, string boundary = "")
        {
            using var process = Start(mode, boundary); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); Assert.True(process.ExitCode is 0 or 86, await stdout + await stderr); return process.ExitCode; }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
    }

    private static void PublishGcCatalog(string cache, PlatformFixtureData fixture, Action<string>? checkpoint = null) => SunderRpcPlatformCatalogCache.Publish(cache,
        new Dictionary<string, byte[]> { [fixture.Requirement.ContentIdentity] = fixture.Envelope }, SunderRpcPlatformCatalogCache.Requirements(fixture.Lock), checkpoint: checkpoint);
    private static void AgeCatalogTree(string cache)
    {
        var old = DateTime.UtcNow.AddDays(-10);
        foreach (var file in Directory.GetFiles(cache, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file, old);
        foreach (var directory in Directory.GetDirectories(cache, "*", SearchOption.AllDirectories)) Directory.SetLastWriteTimeUtc(directory, old);
    }
    private void ClearCatalogGcLock() => File.Copy(Fixture("authoring/conformance/valid/minimal-contract-lock.json"), LockPath, overwrite: true);
    private void SelectMetadataCatalog()
    {
        var locked = ReadJson(Fixture("authoring/conformance/valid/mixed-contract-lock.json")); locked["platformImports"] = new JsonObject(); WriteJson(LockPath, locked);
    }
}
