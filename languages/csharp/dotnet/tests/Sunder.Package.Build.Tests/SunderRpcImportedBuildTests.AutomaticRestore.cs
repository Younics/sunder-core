using System.Xml.Linq;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Xunit;
using TaskItem = Microsoft.Build.Utilities.TaskItem;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcImportedBuildTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticRestore_InitializesEmptyDirectoriesButNeverAdoptsUnmarkedFiles(bool unmarkedFiles)
    {
        var archive = await CreateLockedCandidateAsync();
        var cache = Directory.CreateDirectory(Path.Combine(_root, "uninitialized-cache")).FullName;
        if (unmarkedFiles) File.WriteAllText(Path.Combine(cache, "unowned.txt"), "preserve\n");
        var before = Snapshot(cache);
        var task = new RestoreSunderContractsTask
        {
            BuildEngine = new BuildEngine(), ProjectDirectory = _root, ContractCacheRoot = cache,
            CandidateArchives = [new TaskItem(archive)], OnlyMissing = true,
        };
        Assert.Equal(!unmarkedFiles, task.Execute());
        if (unmarkedFiles) Assert.Equal(before, Snapshot(cache));
        else Assert.Single(SunderRpcContractCacheSelection.Resolve(SunderRpcAuthoringDocuments.ReadContractLock(LockPath), cache).Cache.EntriesByDescriptorSha256);
    }

    [Theory]
    [InlineData("descriptor")]
    [InlineData("receipt")]
    [InlineData("projection")]
    [InlineData("projection-receipt")]
    [InlineData("catalog")]
    [InlineData("catalog-receipt")]
    [InlineData("pending")]
    [InlineData("marker")]
    public async Task AutomaticRestore_PreservesCorruptSurvivorsEvenWhenOtherEvidenceIsMissing(string failure)
    {
        var archive = await CreateLockedCandidateAsync();
        var selected = ReadJson(LockPath);
        selected["packageArtifacts"]![DependencyId]!.AsObject().Remove("registryOrigin");
        var platform = PlatformFixture();
        selected["platformImports"] = ReadJson(LockPath)["platformImports"]!.DeepClone();
        foreach (var import in selected["platformImports"]!.AsObject()) import.Value!.AsObject().Remove("registryOrigin");
        WriteJson(LockPath, selected);
        var locked = File.ReadAllBytes(LockPath);
        var cache = Path.Combine(_root, "automatic-integrity");
        var task = new RestoreSunderContractsTask
        {
            BuildEngine = new BuildEngine(), ProjectDirectory = _root, ContractCacheRoot = cache,
            CandidateArchives = [new TaskItem(archive)], PlatformCatalogPath = platform.Path, OnlyMissing = true,
        };
        AssertSuccess(task);
        var artifact = SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PackageArtifacts[DependencyId];
        var descriptor = SunderRpcContractCache.GetDescriptorPath(cache, _descriptor.Sha256);
        var projection = SunderRpcContractCache.GetModelProjectionPath(cache, artifact.ModelProjectionSha256);
        var catalog = SunderRpcPlatformCatalogCache.CatalogPath(cache, platform.Requirement.ContentIdentity);
        var damaged = failure switch
        {
            "descriptor" => descriptor,
            "receipt" => SunderRpcContractCache.GetReceiptPath(cache, _descriptor.Sha256),
            "projection" => projection,
            "projection-receipt" => Path.ChangeExtension(projection, "receipt.json"),
            "catalog" => catalog,
            "catalog-receipt" => Path.ChangeExtension(catalog, "receipt.json"),
            "marker" => Path.Combine(cache, SunderRpcContractCacheStore.Marker),
            _ => Path.Combine(Directory.CreateDirectory(Path.Combine(cache, ".pending")).FullName, "unowned.txt"),
        };
        File.WriteAllText(damaged, "preserve corrupt or incomplete evidence\n");
        File.Delete(failure == "descriptor" ? projection : descriptor);
        var before = Snapshot(cache);
        Assert.False(task.Execute());
        Assert.Contains(((BuildEngine)task.BuildEngine).Errors, error => error.Contains("contract-cache.entry-corrupt", StringComparison.Ordinal));
        Assert.Equal(before, Snapshot(cache));
        Assert.Equal(locked, File.ReadAllBytes(LockPath));
    }

    [Fact]
    public async Task AutomaticRestore_CancelsWhileWaitingForTheSharedCacheLease()
    {
        var task = new RestoreSunderContractsTask
        {
            BuildEngine = new BuildEngine(), ProjectDirectory = _root, ContractCacheRoot = _cache, OnlyMissing = true,
        };
        var before = Snapshot(_cache);
        using (GeneratedOutputLock.Acquire([_cache]))
        {
            var running = Task.Run(task.Execute);
            Assert.NotSame(running, await Task.WhenAny(running, Task.Delay(200)));
            task.Cancel();
            Assert.False(await running.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(before, Snapshot(_cache));
        Assert.Contains(((BuildEngine)task.BuildEngine).Errors, error => error.Contains("OperationCanceledException", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task Msbuild_SolutionRestoreSharesExactCacheAndSkipsOptOutAndDesignTimeRequests()
    {
        var archive = await CreateLockedCandidateAsync();
        var selected = ReadJson(LockPath);
        selected["packageArtifacts"]![DependencyId]!.AsObject().Remove("registryOrigin");
        WriteJson(LockPath, selected);
        var locked = File.ReadAllBytes(LockPath);
        var cache = Path.Combine(_root, "graph-cache");
        var first = CreateMsbuildImportProject(cache);
        var document = XDocument.Load(first);
        document.Root!.Element("PropertyGroup")!.Add(new XElement("SunderPackageCandidatePath", archive));
        document.Save(first);
        var peer = Directory.CreateDirectory(Path.Combine(_root, "peer")).FullName;
        var second = Path.Combine(peer, "Peer.csproj");
        document.Save(second);
        File.Copy(LockPath, Path.Combine(peer, "sunder-contracts.lock.json"));

        await RunDotnet("restore", first, "-p:SunderRestoreContracts=false", "--nologo");
        Assert.False(Directory.Exists(cache));
        await RunDotnet("restore", first, "-p:DesignTimeBuild=true", "--nologo");
        Assert.False(Directory.Exists(cache));
        var solution = Path.Combine(_root, "RestoreGraph.slnx");
        new XDocument(new XElement("Solution",
            new XElement("Project", new XAttribute("Path", Path.GetFileName(first))),
            new XElement("Project", new XAttribute("Path", "peer/Peer.csproj")))).Save(solution);
        await RunDotnet("restore", solution, "-m:2", "--nologo");
        Assert.Single(SunderRpcContractCache.Resolve(SunderRpcAuthoringDocuments.ReadContractLock(LockPath), cache).EntriesByDescriptorSha256);
        Assert.Equal(locked, File.ReadAllBytes(LockPath));
        Assert.Equal(locked, File.ReadAllBytes(Path.Combine(peer, "sunder-contracts.lock.json")));
        Assert.False(Directory.Exists(Path.Combine(peer, "bin")));
    }
}
