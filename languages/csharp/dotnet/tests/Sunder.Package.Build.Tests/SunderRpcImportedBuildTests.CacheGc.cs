using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcImportedBuildTests
{
    [ExternalArtifactFact("SUNDER_REQUIRE_NODE_OUTPUT_LOCK_INTEROP")]
    [Trait("Category", "ExternalArtifact")]
    public async Task CacheGc_CrossLanguageCrashRecoveryAndRestoreContentionUseOneProtocol()
    {
        var helper = Path.Combine(RepositoryRoot(), "languages/typescript/node/packages/package-tool/dist-test/test/contract-cache-child.js");
        Assert.True(File.Exists(helper));
        var descriptorFile = Path.Combine(_root, "node-descriptor.json");
        File.WriteAllBytes(descriptorFile, _descriptor.GetCanonicalUtf8());
        var first = AddUnusedCacheEntry("example.gcone"); AgeEntry(first);
        Assert.Equal(86, await Node("gc-crash"));
        Assert.Equal(1, SunderRpcContractCacheStore.Collect(_cache, [LockPath]).RemovedEntries);

        var referenced = AddUnusedCacheEntry("example.gctwo"); AgeEntry(referenced);
        Assert.Throws<IOException>(() => SunderRpcContractCacheStore.Collect(_cache, [LockPath], checkpoint: point =>
        { if (point == "gc-moved") throw new IOException("C# GC interrupted."); }));
        SelectCachedDescriptor(referenced);
        File.WriteAllBytes(descriptorFile, referenced.GetCanonicalUtf8());
        Assert.Equal(0, await Node("gc"));
        Assert.True(File.Exists(SunderRpcContractCache.GetDescriptorPath(_cache, referenced.Sha256)));
        using (var held = GeneratedOutputLock.Acquire([_cache]))
        using (var waiting = Start("gc"))
        {
            try
            {
                Assert.Equal("collecting", await waiting.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                await Task.Delay(200); Assert.False(waiting.HasExited); held.Dispose();
                await waiting.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(waiting.ExitCode == 0, await waiting.StandardError.ReadToEndAsync());
            }
            finally { if (!waiting.HasExited) waiting.Kill(entireProcessTree: true); }
        }

        var third = AddUnusedCacheEntry("example.gcthree"); AgeEntry(third);
        using var prepared = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var collecting = Task.Run(() => SunderRpcContractCacheStore.Collect(_cache, [LockPath], checkpoint: point =>
        { if (point == "gc-prepared") { prepared.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); } }));
        Assert.True(prepared.Wait(TimeSpan.FromSeconds(10)));
        using (var restoring = Start("publish"))
        {
            try
            {
                Assert.Equal("publishing", await restoring.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                await Task.Delay(200); Assert.False(restoring.HasExited); release.Set();
                await collecting.WaitAsync(TimeSpan.FromSeconds(10));
                await restoring.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(restoring.ExitCode == 0, await restoring.StandardError.ReadToEndAsync());
            }
            finally { release.Set(); if (!restoring.HasExited) restoring.Kill(entireProcessTree: true); }
        }
        Assert.False(File.Exists(SunderRpcContractCache.GetDescriptorPath(_cache, third.Sha256)));
        Assert.Single(SunderRpcContractCache.Resolve(SunderRpcAuthoringDocuments.ReadContractLock(LockPath), _cache).EntriesByDescriptorSha256);

        Process Start(string mode)
        {
            var info = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { helper, mode, LockPath, _cache, descriptorFile }) info.ArgumentList.Add(value);
            return Process.Start(info)!;
        }
        async Task<int> Node(string mode)
        {
            using var process = Start(mode);
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); Assert.True(process.ExitCode is 0 or 86, await stdout + await stderr); return process.ExitCode; }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
    }

    [Fact]
    public void CacheGc_ProtectsReviewedAndYoungEntriesWithoutChangingLocksOrRetainedMtimes()
    {
        var unused = AddUnusedCacheEntry("example.unused");
        var young = AddUnusedCacheEntry("example.young");
        AgeEntry(_descriptor); AgeEntry(unused);
        var source = File.ReadAllBytes(LockPath); var sourceTime = File.GetLastWriteTimeUtc(LockPath);
        var retainedTime = File.GetLastWriteTimeUtc(SunderRpcContractCache.GetDescriptorPath(_cache, _descriptor.Sha256));
        var result = SunderRpcContractCacheStore.Collect(_cache, [LockPath, LockPath]);
        Assert.Equal(1, result.RemovedEntries);
        Assert.Equal(0, result.RemovedArtifacts);
        Assert.False(File.Exists(SunderRpcContractCache.GetDescriptorPath(_cache, unused.Sha256)));
        Assert.True(File.Exists(SunderRpcContractCache.GetDescriptorPath(_cache, young.Sha256)));
        Assert.Equal(retainedTime, File.GetLastWriteTimeUtc(SunderRpcContractCache.GetDescriptorPath(_cache, _descriptor.Sha256)));
        Assert.Equal(source, File.ReadAllBytes(LockPath)); Assert.Equal(sourceTime, File.GetLastWriteTimeUtc(LockPath));
        Assert.Single(SunderRpcContractCache.Resolve(SunderRpcAuthoringDocuments.ReadContractLock(LockPath), _cache).EntriesByDescriptorSha256);
        var before = Snapshot(_cache);
        Assert.Equal(0, SunderRpcContractCacheStore.Collect(_cache, [LockPath]).RemovedEntries);
        Assert.Equal(before, Snapshot(_cache));
    }

    [Theory]
    [InlineData("gc-prepared")]
    [InlineData("gc-tombstoned")]
    [InlineData("gc-moved")]
    [InlineData("gc-committed")]
    [InlineData("gc-deleted")]
    public void CacheGc_ResumesEveryTombstoneAndDeletionBoundary(string point)
    {
        var unused = AddUnusedCacheEntry("example.unused"); AgeEntry(unused);
        Assert.Throws<IOException>(() => SunderRpcContractCacheStore.Collect(_cache, [LockPath], checkpoint: step =>
        { if (step == point || step.StartsWith(point + ":", StringComparison.Ordinal)) throw new IOException("Injected GC interruption."); }));
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        if (point is not ("gc-committed" or "gc-deleted"))
        {
            Assert.Contains("collection is incomplete", Assert.Throws<InvalidDataException>(() => SunderRpcContractCache.Resolve(contractLock, _cache)).Message);
            Assert.Throws<InvalidDataException>(() => SunderRpcContractCacheStore.Publish(_cache,
                new Dictionary<string, byte[]> { [_descriptor.Sha256] = _descriptor.GetCanonicalUtf8() }, SunderRpcContractCache.GetRequirements(contractLock)));
        }
        SunderRpcContractCacheStore.Collect(_cache, [LockPath]);
        Assert.False(File.Exists(SunderRpcContractCache.GetDescriptorPath(_cache, unused.Sha256)));
        Assert.False(File.Exists(SunderRpcContractCache.GetReceiptPath(_cache, unused.Sha256)));
        Assert.Single(SunderRpcContractCache.Resolve(contractLock, _cache).EntriesByDescriptorSha256);
        Assert.Empty(Directory.GetDirectories(_cache, ".gc-*"));
    }

    [Fact]
    public void CacheGc_RecoveryRestoresAnEntryNewlyReferencedByAReviewedLock()
    {
        var unused = AddUnusedCacheEntry("example.newreference"); AgeEntry(unused);
        Assert.Throws<IOException>(() => SunderRpcContractCacheStore.Collect(_cache, [LockPath], checkpoint: step =>
        { if (step == "gc-moved") throw new IOException("Interrupted after tombstoning."); }));
        SelectCachedDescriptor(unused);
        var source = File.ReadAllBytes(LockPath);
        Assert.Equal(0, SunderRpcContractCacheStore.Collect(_cache, [LockPath]).RemovedEntries);
        Assert.Equal(source, File.ReadAllBytes(LockPath));
        var entry = Assert.Single(SunderRpcContractCache.Resolve(SunderRpcAuthoringDocuments.ReadContractLock(LockPath), _cache).EntriesByDescriptorSha256);
        Assert.Equal(unused.Sha256, entry.Key);
    }

    [Fact]
    public void CacheGc_CollectsAgedProvenQuarantineButRetainsUnprovenStages()
    {
        File.WriteAllText(SunderRpcContractCache.GetDescriptorPath(_cache, _descriptor.Sha256), "bad bytes to preserve through repair");
        var value = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        SunderRpcContractCacheStore.Publish(_cache, new Dictionary<string, byte[]> { [_descriptor.Sha256] = _descriptor.GetCanonicalUtf8() }, SunderRpcContractCache.GetRequirements(value));
        var quarantine = Assert.Single(Directory.GetDirectories(Path.Combine(_cache, ".quarantine")));
        var unproven = Path.Combine(_cache, ".stage-" + Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(unproven); File.WriteAllText(Path.Combine(unproven, "user.txt"), "keep me");
        foreach (var directory in new[] { quarantine, unproven })
        {
            foreach (var file in Directory.GetFiles(directory)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-10));
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-10));
        }
        var result = SunderRpcContractCacheStore.Collect(_cache, [LockPath]);
        Assert.Equal(1, result.RemovedArtifacts); Assert.Equal(1, result.RetainedUnprovenArtifacts);
        Assert.False(Directory.Exists(quarantine)); Assert.Equal("keep me", File.ReadAllText(Path.Combine(unproven, "user.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CacheGc_CollectsOwnershipProvenRestoreStagesAndCompletionArtifacts(bool completed)
    {
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        File.Delete(SunderRpcContractCache.GetReceiptPath(_cache, _descriptor.Sha256));
        Assert.Throws<IOException>(() => SunderRpcContractCacheStore.Publish(_cache,
            new Dictionary<string, byte[]> { [_descriptor.Sha256] = _descriptor.GetCanonicalUtf8() }, SunderRpcContractCache.GetRequirements(contractLock),
            checkpoint: point => { if (point == (completed ? "committed" : "prepared")) throw new IOException("Interrupted restore."); }));
        string artifact;
        if (completed) artifact = Assert.Single(Directory.GetDirectories(_cache, ".completed-*"));
        else
        {
            var pending = Path.Combine(_cache, SunderRpcContractCacheStore.Pending);
            var id = ReadJson(Path.Combine(pending, "intent.json"))["token"]!.GetValue<string>();
            artifact = Path.Combine(_cache, ".stage-" + id); Directory.Move(pending, artifact);
            File.Copy(SunderRpcContractCache.GetReceiptPath(artifact, _descriptor.Sha256), SunderRpcContractCache.GetReceiptPath(_cache, _descriptor.Sha256));
        }
        foreach (var file in Directory.GetFiles(artifact)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-10));
        Directory.SetLastWriteTimeUtc(artifact, DateTime.UtcNow.AddDays(-10));
        Assert.Equal(1, SunderRpcContractCacheStore.Collect(_cache, [LockPath]).RemovedArtifacts);
        Assert.False(Directory.Exists(artifact));
        Assert.Single(SunderRpcContractCache.Resolve(contractLock, _cache).EntriesByDescriptorSha256);
    }

    [Theory]
    [InlineData("missing-lock")]
    [InlineData("malformed-lock")]
    [InlineData("linked-entry")]
    [InlineData("grace-zero")]
    [InlineData("no-locks")]
    [InlineData("wrong-recovery-locks")]
    [InlineData("unexpected-tombstone-file")]
    [InlineData("intent-traversal")]
    public void CacheGc_InvalidInputsAndPathsDoNotDeleteCacheBytes(string failure)
    {
        var unused = AddUnusedCacheEntry("example.unused"); AgeEntry(unused);
        var reviewed = LockPath;
        if (failure == "missing-lock") reviewed = Path.Combine(_root, "missing.json");
        if (failure == "malformed-lock") File.WriteAllText(LockPath, "{}");
        if (failure == "linked-entry")
        {
            var path = SunderRpcContractCache.GetDescriptorPath(_cache, unused.Sha256); var outside = Path.Combine(_root, "outside.json");
            File.Move(path, outside); File.CreateSymbolicLink(path, outside);
        }
        if (failure is "wrong-recovery-locks" or "unexpected-tombstone-file" or "intent-traversal")
        {
            Assert.Throws<IOException>(() => SunderRpcContractCacheStore.Collect(_cache, [LockPath], checkpoint: step =>
            { if (step == "gc-prepared") throw new IOException("Interrupted."); }));
            if (failure == "wrong-recovery-locks")
            { reviewed = Path.Combine(_root, "other.json"); File.Copy(LockPath, reviewed); }
            else if (failure == "unexpected-tombstone-file") File.WriteAllText(Path.Combine(_cache, SunderRpcContractCacheStore.CollectionPending, "unowned.txt"), "keep me");
            else
            {
                var path = Path.Combine(_cache, SunderRpcContractCacheStore.CollectionPending, "intent.json");
                var value = ReadJson(path); value["units"]![0]!["files"]![0]!["name"] = "../outside.json"; WriteJson(path, value);
            }
        }
        var before = Snapshot(_cache);
        Assert.NotNull(Record.Exception(() => SunderRpcContractCacheStore.Collect(_cache, failure == "no-locks" ? [] : [reviewed], failure == "grace-zero" ? 0 : 7)));
        Assert.Equal(before, Snapshot(_cache));
    }

    [Fact]
    public void CacheGc_BatchesLargeSetsAndReportsRemainingWork()
    {
        for (var index = 0; index < 129; index++) AgeEntry(AddUnusedCacheEntry("example.unused" + index));
        var first = SunderRpcContractCacheStore.Collect(_cache, [LockPath]);
        Assert.Equal(128, first.RemovedEntries); Assert.True(first.MoreCandidates);
        var second = SunderRpcContractCacheStore.Collect(_cache, [LockPath]);
        Assert.Equal(1, second.RemovedEntries); Assert.False(second.MoreCandidates);
    }

    [Fact]
    public void CacheGc_CollectsACompleteAgedGcStageWithoutReplayingItsOldSelection()
    {
        var unused = AddUnusedCacheEntry("example.unused"); AgeEntry(unused);
        Assert.Throws<IOException>(() => SunderRpcContractCacheStore.Collect(_cache, [LockPath], checkpoint: point =>
        { if (point == "gc-prepared") throw new IOException("Interrupted before moves."); }));
        var pending = Path.Combine(_cache, SunderRpcContractCacheStore.CollectionPending);
        var id = ReadJson(Path.Combine(pending, "intent.json"))["token"]!.GetValue<string>();
        var stage = Path.Combine(_cache, ".gc-stage-" + id); Directory.Move(pending, stage);
        File.SetLastWriteTimeUtc(Path.Combine(stage, "intent.json"), DateTime.UtcNow.AddDays(-10));
        Directory.SetLastWriteTimeUtc(stage, DateTime.UtcNow.AddDays(-10));
        SelectCachedDescriptor(unused);
        var result = SunderRpcContractCacheStore.Collect(_cache, [LockPath]);
        Assert.Equal(0, result.RemovedEntries); Assert.Equal(1, result.RemovedArtifacts);
        Assert.True(File.Exists(SunderRpcContractCache.GetDescriptorPath(_cache, unused.Sha256)));
        Assert.False(Directory.Exists(stage));
    }

    [Fact]
    public void CacheGc_SourceEditAtTheCommitFencePreventsDeletionAndRecoveryUsesNewMarks()
    {
        var unused = AddUnusedCacheEntry("example.newreference"); AgeEntry(unused);
        Assert.NotNull(Record.Exception(() => SunderRpcContractCacheStore.Collect(_cache, [LockPath], checkpoint: point =>
        { if (point == "gc-moved") SelectCachedDescriptor(unused); })));
        Assert.True(Directory.Exists(Path.Combine(_cache, SunderRpcContractCacheStore.CollectionPending)));
        Assert.Equal(0, SunderRpcContractCacheStore.Collect(_cache, [LockPath]).RemovedEntries);
        Assert.Equal(unused.Sha256, Assert.Single(SunderRpcContractCache.Resolve(SunderRpcAuthoringDocuments.ReadContractLock(LockPath), _cache).EntriesByDescriptorSha256).Key);
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task CacheGc_MsbuildRequiresExplicitReviewedLocksAndReportsPrunedEntries()
    {
        var unused = AddUnusedCacheEntry("example.unused"); AgeEntry(unused);
        var project = Path.Combine(_root, "GcOnly.proj");
        new XDocument(new XElement("Project", new XElement("PropertyGroup",
            new XElement("SunderPackageBuildTaskAssembly", typeof(CollectSunderContractCacheTask).Assembly.Location),
            new XElement("SunderPackageBuildTaskSourceIsImmutable", "true"), new XElement("SunderPackageBuildProject", Path.Combine(_root, "no-source.csproj")),
            new XElement("SunderContractCacheRoot", _cache), new XElement("BaseIntermediateOutputPath", "gc-obj/")),
            new XElement("Import", new XAttribute("Project", Path.Combine(RepositoryRoot(), "languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Package.Build.targets"))))).Save(project);
        var missing = await RunDotnet(false, "msbuild", project, "-t:CollectSunderContractCache", "-nr:false", "-nologo");
        Assert.Contains("explicit SunderContractCacheLocks", missing, StringComparison.Ordinal);
        var result = await RunDotnet("msbuild", project, "-t:CollectSunderContractCache", "-nr:false", "-nologo", "-p:SunderContractCacheLocks=" + LockPath);
        Assert.Contains("Collected 1 contract entries", result, StringComparison.Ordinal);
        Assert.Contains("Pruned entries require explicit restore", result, StringComparison.Ordinal);
        foreach (var option in new[] { "SunderRegistryOrigin=https://registry.example/", "SunderAllowDevelopmentRegistryOrigin=true" })
        {
            var invalid = await RunDotnet(false, "msbuild", project, "-t:CollectSunderContractCache", "-nr:false", "-nologo", "-p:SunderContractCacheLocks=" + LockPath, "-p:" + option);
            Assert.Contains("GC does not accept Registry or selection inputs", invalid, StringComparison.Ordinal);
        }
    }

    private SunderRpcContractDescriptor AddUnusedCacheEntry(string id)
    {
        var json = JsonNode.Parse(_descriptor.GetCanonicalUtf8())!; json["contractId"] = id;
        var descriptor = SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(json));
        var requirement = new SunderRpcContractCacheRequirement(descriptor.ContractId, descriptor.Version, descriptor.GetCanonicalUtf8().Length, descriptor.Sha256, descriptor.AbiSha256);
        SunderRpcContractCacheStore.Publish(_cache, new Dictionary<string, byte[]> { [descriptor.Sha256] = descriptor.GetCanonicalUtf8() },
            new Dictionary<string, SunderRpcContractCacheRequirement> { [descriptor.Sha256] = requirement });
        return descriptor;
    }

    private void AgeEntry(SunderRpcContractDescriptor descriptor)
    {
        File.SetLastWriteTimeUtc(SunderRpcContractCache.GetDescriptorPath(_cache, descriptor.Sha256), DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(SunderRpcContractCache.GetReceiptPath(_cache, descriptor.Sha256), DateTime.UtcNow.AddDays(-10));
    }

    private void SelectCachedDescriptor(SunderRpcContractDescriptor descriptor)
    {
        var json = ReadJson(LockPath); var artifact = json["packageArtifacts"]![DependencyId]!;
        var contract = artifact["contracts"]![ContractId]!.DeepClone();
        contract["resolvedContractVersion"] = descriptor.Version; contract["descriptorBytes"] = descriptor.GetCanonicalUtf8().Length;
        contract["descriptorSha256"] = descriptor.Sha256; contract["abiSha256"] = descriptor.AbiSha256; contract["etag"] = "\"" + descriptor.Sha256 + "\"";
        artifact["contracts"] = new JsonObject { [descriptor.ContractId] = contract };
        artifact["publicInventorySha256"] = InventorySha256(DependencyId, artifact);
        json["packageImports"]![DependencyId]!["requiredContracts"] = new JsonArray(descriptor.ContractId);
        WriteJson(LockPath, json);
    }
}
