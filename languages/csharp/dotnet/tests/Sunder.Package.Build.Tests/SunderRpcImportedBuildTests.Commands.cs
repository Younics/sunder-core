using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcImportedBuildTests
{
    [Fact]
    public async Task PackageApiCommands_FreshCompleteMultiProviderSourceConfiguresImportsBeforeFirstAccept()
    {
        var archive = await CreateLockedCandidateAsync();
        var platform = PlatformFixture();
        File.Copy(Fixture("authoring/conformance/valid/minimal-contract-lock.json"), LockPath, overwrite: true);
        var ownedPath = Path.Combine(_root, SunderRpcAuthoringDocuments.OwnedContractsFileName);
        File.Delete(ownedPath); File.Delete(Path.Combine(_root, "Consumer.cs"));
        File.WriteAllText(Path.Combine(_root, "Reviewed.cs"), """
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)]
            public sealed partial class ReviewedService { public ValueTask<string> EchoAsync(string value) => ValueTask.FromResult(value); }
            """);
        File.WriteAllText(Path.Combine(_root, "Provider.cs"), ProviderSource);
        File.WriteAllText(Path.Combine(_root, "PlatformProvider.cs"), PlatformProviderSource
            .Replace("IMinimalProviderService", "IStackContributorService", StringComparison.Ordinal)
            .Replace("CExample_Rpc_V1_Minimal", "CSunder_Stack_Contributor", StringComparison.Ordinal)
            .Replace("LocalProvider", "LocalStackProvider", StringComparison.Ordinal)
            .Replace("ProviderRegistrationProbe", "StackRegistrationProbe", StringComparison.Ordinal)
            .Replace("example.owner.minimal.provider", "example.owner.stack.provider", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(_root, "Program.cs"), WorkerSource.Replace("new SunderWorkerOptions([])",
            "new SunderWorkerOptions([new LocalProvider().CreateSunderRpcProviderRegistration(), new LocalStackProvider().CreateSunderRpcProviderRegistration()])", StringComparison.Ordinal));
        using var handler = new RestoreHandler(_ => throw new InvalidOperationException("Local initial configuration attempted HTTP."));
        using var http = new HttpClient(handler); var commands = new SunderRpcPackageApiCommands(new(http));
        Task<SunderRpcPackageApiResult> Select(SunderRpcPackageApiRequest request) => commands.ExecuteAsync(LockPath, _cache, request,
            (before, _, stage) => CreatePackageApiTask(request.Operation).ValidateSource(before, stage, _cache, request));
        Assert.True((await Select(new("Implement", DependencyId, ContractId, ">=1.0.0 <2.0.0", Candidate: archive, ProviderKey: "minimalProvider"))).Changed);
        var incomplete = CreateGeneration(); incomplete.ContractOperation = "Accept";
        Assert.False(incomplete.Execute()); Assert.False(File.Exists(ownedPath));
        Assert.True((await Select(new("PlatformImplement", ContractId: "sunder.stack.contributor", PlatformCatalog: platform.Path))).Changed);
        Assert.False(File.Exists(ownedPath)); Assert.False(Directory.Exists(Path.Combine(CreateGeneration().IntermediateOutputPath, "sunder-rpc")));
        var completeLock = File.ReadAllBytes(LockPath);
        var sourcePath = Path.Combine(_root, "Provider.cs"); var source = File.ReadAllText(sourcePath);
        File.WriteAllText(sourcePath, source.Replace("ValueTask.CompletedTask", "ValueTask.FromResult(42)", StringComparison.Ordinal));
        var invalid = CreateGeneration(); invalid.ContractOperation = "Accept";
        Assert.False(invalid.Execute()); Assert.False(File.Exists(ownedPath)); Assert.Equal(completeLock, File.ReadAllBytes(LockPath));
        File.WriteAllText(sourcePath, source);
        var accept = CreateGeneration(); accept.ContractOperation = "Accept"; AssertSuccess(accept);
        var generation = Generate(); Assert.Equal(3, generation.RpcProviders.Length);
        File.WriteAllText(sourcePath, source.Replace("ValueTask.CompletedTask", "ValueTask.FromResult(42)", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => Select(new("Update", DependencyId, Candidate: archive, PlatformCatalog: platform.Path)));
        Assert.Equal(completeLock, File.ReadAllBytes(LockPath));
        File.Delete(ownedPath);
        // Prior output in a different configuration/RID remains evidence even
        // when the current neutral tree and its adjacent marker were removed.
        var oldOutput = Path.GetDirectoryName(Assert.Single(generation.AuthoringReceipts).ItemSpec)!;
        var retainedOutput = Path.Combine(_root, "obj/Other/net10.0/sunder-analysis/osx-arm64/sunder-rpc");
        Directory.CreateDirectory(Path.GetDirectoryName(retainedOutput)!);
        Directory.Move(oldOutput, retainedOutput);
        File.Move(GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(oldOutput), GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(retainedOutput));
        await Assert.ThrowsAsync<InvalidDataException>(() => Select(new("PlatformUpdate", ContractId: "sunder.stack.contributor", PlatformCatalog: platform.Path)));
        File.WriteAllText(sourcePath, source);
        var resurrect = CreateGeneration(); resurrect.ContractOperation = "Accept"; Assert.False(resurrect.Execute()); Assert.False(File.Exists(ownedPath));
        Assert.Equal(completeLock, File.ReadAllBytes(LockPath)); Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task PackageApiCommands_LocalCandidateSelectsBuildsAndRestoresWithoutRegistry()
    {
        var archive = await CreateLockedCandidateAsync();
        File.Copy(Fixture("authoring/conformance/valid/minimal-contract-lock.json"), LockPath, overwrite: true);
        var ownedPath = Path.Combine(_root, SunderRpcAuthoringDocuments.OwnedContractsFileName);
        File.Delete(ownedPath);
        using var handler = new RestoreHandler(_ => throw new InvalidOperationException("Local authoring attempted HTTP."));
        using var http = new HttpClient(handler);
        var cache = Path.Combine(_root, "local-selection-cache");
        var commands = new SunderRpcPackageApiCommands(new(http));
        var request = new SunderRpcPackageApiRequest("Add", DependencyId, VersionRange: ">=1.0.0 <2.0.0", Candidate: archive);
        var selected = await commands.ExecuteAsync(LockPath, cache, request,
            (before, _, stage) => CreatePackageApiTask("Add").ValidateSource(before, stage, cache, request));
        Assert.True(selected.Changed);
        Assert.Null(selected.ContractLock.PackageArtifacts[DependencyId].RegistryOrigin);
        Assert.DoesNotContain("registryOrigin", File.ReadAllText(LockPath), StringComparison.Ordinal);
        Assert.DoesNotContain(archive, File.ReadAllText(LockPath), StringComparison.Ordinal);
        Assert.False(File.Exists(ownedPath));
        var accept = CreateGeneration(); accept.ContractCacheRoot = cache; accept.ContractOperation = "Accept";
        AssertSuccess(accept);
        var generation = CreateGeneration(); generation.ContractCacheRoot = cache;
        AssertSuccess(generation);
        var pinned = File.ReadAllBytes(LockPath);
        var cold = Path.Combine(_root, "local-cold-cache");
        var restore = new SunderRpcContractRestore(http);
        Assert.Equal(1, await restore.RestoreAsync(LockPath, cold, [archive]));
        Assert.Equal(0, await restore.RestoreAsync(LockPath, cold, []));
        Assert.Equal(pinned, File.ReadAllBytes(LockPath));
        Assert.Equal(0, handler.Requests);
        var missing = await Assert.ThrowsAsync<InvalidDataException>(() => commands.ExecuteAsync(LockPath, cache, new("Update", DependencyId)));
        Assert.StartsWith("package-api.local-input-required:", missing.Message);
        Assert.Equal(pinned, File.ReadAllBytes(LockPath));
    }

    [Fact]
    public async Task PackageApiCommands_OfflineReadinessAndRemovalPreserveProviderIntentAndCache()
    {
        var document = ReadJson(LockPath);
        document["providerImports"]!["minimalProvider"] = new JsonObject { ["packageId"] = DependencyId, ["contractId"] = ContractId };
        WriteJson(LockPath, document);
        var cache = Snapshot(_cache);
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException("Offline command attempted HTTP.")));
        var commands = new SunderRpcPackageApiCommands(new(http));
        var before = File.ReadAllBytes(LockPath);
        var repeat = await commands.ExecuteAsync(LockPath, _cache, new("Require", DependencyId, ContractId));
        Assert.False(repeat.Changed);
        Assert.Equal(before, File.ReadAllBytes(LockPath));
        var refusal = await Assert.ThrowsAsync<InvalidDataException>(() => commands.ExecuteAsync(LockPath, _cache, new("Remove", DependencyId)));
        Assert.StartsWith("package-api.remove-still-referenced:", refusal.Message);
        Assert.Equal(before, File.ReadAllBytes(LockPath));
        Assert.True((await commands.ExecuteAsync(LockPath, _cache, new("Unrequire", DependencyId, ContractId))).Changed);
        Assert.False((await commands.ExecuteAsync(LockPath, _cache, new("Unrequire", DependencyId, ContractId))).Changed);
        var removed = await commands.ExecuteAsync(LockPath, _cache, new("Remove", DependencyId));
        Assert.Empty(removed.ContractLock.PackageImports);
        Assert.Single(removed.ContractLock.ProviderImports);
        Assert.Single(removed.ContractLock.PackageArtifacts);
        var unimplemented = await commands.ExecuteAsync(LockPath, _cache, new("Unimplement", DependencyId, ContractId), (_, _, _) => { });
        Assert.Empty(unimplemented.ContractLock.PackageArtifacts);
        Assert.Empty(unimplemented.ContractLock.ProviderImports);
        Assert.Equal(cache, Snapshot(_cache));
        Assert.False((await commands.ExecuteAsync(LockPath, _cache, new("List"))).Changed);
        Assert.Empty(Directory.GetFiles(_root, "*.stage-*"));
    }

    [Theory]
    [InlineData(null, "1.9.0", false)]
    [InlineData(true, "1.10.0-rc.1", true)]
    [InlineData(false, "1.9.0", false)]
    public async Task PackageApiCommands_TargetedUpdateUsesHighestReviewedVersionAndPreservesPresence(bool? prerelease, string selectedVersion, bool expectedPolicy)
    {
        var document = ReadJson(LockPath);
        document["packageImports"]![DependencyId]!["presence"] = "optional";
        if (prerelease is null) document["packageArtifacts"]![DependencyId]!.AsObject().Remove("registryOrigin");
        WriteJson(LockPath, document);
        using var handler = CommandHandler();
        using var http = new HttpClient(handler);
        var cache = Path.Combine(_root, "command-cache");
        var commands = new SunderRpcPackageApiCommands(new(http));
        var result = await commands.ExecuteAsync(LockPath, cache, new("Update", DependencyId, IncludePrerelease: prerelease,
            RegistryOrigin: prerelease is null ? "https://registry.example/" : null));
        Assert.True(result.Changed);
        var artifact = result.ContractLock.PackageArtifacts[DependencyId];
        Assert.Equal(selectedVersion, artifact.SelectedPackageVersion);
        Assert.Equal("https://registry.example/", artifact.RegistryOrigin);
        Assert.Equal(expectedPolicy, artifact.IncludePrerelease);
        Assert.Equal("optional", result.ContractLock.PackageImports[DependencyId].Presence);
        Assert.Equal(new[] { ContractId }, result.ContractLock.PackageImports[DependencyId].RequiredContracts);
        Assert.Single(SunderRpcContractCache.Resolve(result.ContractLock, cache).EntriesByDescriptorSha256);
        Assert.False(Directory.Exists(Path.Combine(_root, "obj")));
        if (prerelease == true)
        {
            File.Delete(SunderRpcContractCache.GetDescriptorPath(cache, _descriptor.Sha256));
            var lockTimestamp = File.GetLastWriteTimeUtc(LockPath);
            Assert.False((await commands.ExecuteAsync(LockPath, cache, new("Update", DependencyId))).Changed);
            Assert.True(File.Exists(SunderRpcContractCache.GetDescriptorPath(cache, _descriptor.Sha256)));
            Assert.Equal(lockTimestamp, File.GetLastWriteTimeUtc(LockPath));
            Assert.True(SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PackageArtifacts[DependencyId].IncludePrerelease);
            Assert.True((await commands.ExecuteAsync(LockPath, cache, new("Update", DependencyId, IncludePrerelease: false, Presence: "required"))).Changed);
            Assert.Equal("required", SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PackageImports[DependencyId].Presence);
        }
    }

    [Fact]
    public async Task PackageApiCommands_AddAndImplementShareSelectionAndLeaveGeneratedOutputForBuild()
    {
        File.WriteAllBytes(LockPath, SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(new
        { lockVersion = 1, packageArtifacts = new { }, packageImports = new { }, providerImports = new { }, platformImports = new { } })));
        using var handler = CommandHandler();
        using var http = new HttpClient(handler);
        var commands = new SunderRpcPackageApiCommands(new(http));
        var cache = Path.Combine(_root, "command-cache");
        var request = new SunderRpcPackageApiRequest("Add", DependencyId, VersionRange: ">=1.0.0 <2.0.0", RegistryOrigin: "https://registry.example/");
        var added = await commands.ExecuteAsync(LockPath, cache, request);
        Assert.Equal("required", added.ContractLock.PackageImports[DependencyId].Presence);
        Assert.Empty(added.ContractLock.PackageImports[DependencyId].RequiredContracts);
        var before = File.ReadAllBytes(LockPath);
        var timestamp = File.GetLastWriteTimeUtc(LockPath);
        var count = handler.Requests;
        Assert.False((await commands.ExecuteAsync(LockPath, cache, request)).Changed);
        Assert.Equal(count, handler.Requests);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(LockPath));
        Assert.Equal(before, File.ReadAllBytes(LockPath));
        var implemented = await commands.ExecuteAsync(LockPath, cache, new("Implement", DependencyId, ContractId));
        Assert.Single(implemented.ContractLock.PackageArtifacts);
        Assert.Single(implemented.ContractLock.PackageImports);
        Assert.Equal("minimalProvider", Assert.Single(implemented.ContractLock.ProviderImports).Key);
        Assert.False(Directory.Exists(Path.Combine(_root, "obj")));
        File.WriteAllText(Path.Combine(_root, "Provider.cs"), ProviderSource);
        var generate = CreateGeneration();
        generate.ContractCacheRoot = cache;
        AssertSuccess(generate);
        Assert.Single(generate.RpcUses);
        Assert.Single(generate.RpcProviders);
    }

    [Theory]
    [InlineData("empty-presence")]
    [InlineData("unknown-presence")]
    [InlineData("origin-override")]
    [InlineData("global-range")]
    [InlineData("global-prerelease")]
    [InlineData("global-presence")]
    [InlineData("global-candidate")]
    [InlineData("absent-package")]
    [InlineData("absent-contract")]
    [InlineData("readiness-selection")]
    public async Task PackageApiCommands_InvalidTransitionInputsAreOfflineAndDoNotChangeLock(string scenario)
    {
        var request = scenario switch
        {
            "empty-presence" => new("Update", DependencyId, Presence: ""),
            "unknown-presence" => new("Update", DependencyId, Presence: "Optional"),
            "origin-override" => new("Update", DependencyId, RegistryOrigin: "https://registry.example/"),
            "global-range" => new("Update", VersionRange: ">=1.0.0"),
            "global-prerelease" => new("Update", IncludePrerelease: false),
            "global-presence" => new("Update", Presence: "required"),
            "global-candidate" => new("Update", Candidate: "/candidate.sunderpkg"),
            "absent-package" => new("Remove", "example.absent"),
            "absent-contract" => new("Require", DependencyId, "example.absent"),
            _ => new SunderRpcPackageApiRequest("Require", DependencyId, ContractId, IncludePrerelease: true),
        };
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException("Invalid command attempted HTTP.")));
        var before = File.ReadAllBytes(LockPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SunderRpcPackageApiCommands(new(http)).ExecuteAsync(LockPath, _cache, request));
        Assert.Equal(before, File.ReadAllBytes(LockPath));
        Assert.False(Directory.Exists(Path.Combine(_root, "obj")));
        Assert.Empty(Directory.GetFiles(_root, "*.stage-*"));
    }

    [Fact]
    public async Task PackageApiCommands_FailedStagedSourceCheckPreservesSelectionAndLastGeneratedFacade()
    {
        var generated = Generate();
        var prior = Snapshot(generated.IntermediateOutputPath);
        using var http = new HttpClient(CommandHandler());
        var before = File.ReadAllBytes(LockPath);
        var cache = Path.Combine(_root, "command-cache");
        Directory.CreateDirectory(cache);
        SunderRpcContractCacheStore.InitializeRoot(cache);
        ModelProjectionTestEvidence.Seed(cache, SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PackageArtifacts[DependencyId]);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SunderRpcPackageApiCommands(new(http)).ExecuteAsync(LockPath, cache,
            new("Update", DependencyId), (_, _, _) => throw new InvalidDataException("staged source failed")));
        Assert.Equal(before, File.ReadAllBytes(LockPath));
        Assert.Equal(prior, Snapshot(generated.IntermediateOutputPath));
        Assert.Single(SunderRpcContractCache.Resolve(SunderRpcAuthoringDocuments.ReadContractLock(LockPath), cache).EntriesByDescriptorSha256);
        Assert.Empty(Directory.GetFiles(_root, "*.stage-*"));
    }

    [Fact]
    public void PackageApiCommands_UnimplementRunsTheActualBoundedFrontend()
    {
        var document = ReadJson(LockPath);
        document["providerImports"]!["minimalProvider"] = new JsonObject { ["packageId"] = DependencyId, ["contractId"] = ContractId };
        WriteJson(LockPath, document);
        var providerPath = Path.Combine(_root, "Provider.cs");
        File.WriteAllText(providerPath, ProviderSource);
        var before = File.ReadAllBytes(LockPath);
        var task = CreatePackageApiTask("Unimplement");
        task.ContractId = ContractId;
        Assert.False(task.Execute());
        Assert.Contains(((BuildEngine)task.BuildEngine).Errors, message => message.Contains("package-api.remove-still-referenced", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllBytes(LockPath));
        File.Delete(providerPath);
        task = CreatePackageApiTask("Unimplement");
        task.ContractId = ContractId;
        AssertSuccess(task);
        var result = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        Assert.Empty(result.ProviderImports);
        Assert.Single(result.PackageImports);
        Assert.Single(result.PackageArtifacts);
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task PackageApiCommands_MsbuildRunsDistinctOfflineTargetsInOneInvocation()
    {
        var project = Path.Combine(_root, "ManageApis.proj");
        new XDocument(new XElement("Project", new XElement("PropertyGroup",
            new XElement("SunderPackageBuildTaskAssembly", typeof(ManageSunderPackageApiTask).Assembly.Location),
            new XElement("SunderPackageBuildTaskSourceIsImmutable", "true"), new XElement("SunderPackageBuildProject", Path.Combine(_root, "no-task-source.csproj")),
            new XElement("BaseIntermediateOutputPath", "command-obj/")),
            new XElement("ItemGroup", new XElement("SunderSdkCapability", new XAttribute("Include", "rpc.v1;storage.v1"))),
            new XElement("Import", new XAttribute("Project", Path.Combine(RepositoryRoot(), "languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Package.Build.targets"))))).Save(project);
        var evaluation = await RunDotnet("msbuild", project, "-nologo", "-nr:false", "-getItem:SunderSdkCapability,AvailableItemName");
        using var evaluated = JsonDocument.Parse(evaluation);
        var items = evaluated.RootElement.GetProperty("Items");
        var capabilities = items.GetProperty("SunderSdkCapability").EnumerateArray().ToArray();
        Assert.Equal(["rpc.v1", "storage.v1"], capabilities.Select(value => value.GetProperty("Identity").GetString()!).ToArray());
        Assert.All(capabilities, value => Assert.Equal("false", value.GetProperty("Visible").GetString()));
        Assert.Equal("false", items.GetProperty("AvailableItemName").EnumerateArray()
            .Single(value => value.GetProperty("Identity").GetString() == "SunderSdkCapability").GetProperty("Visible").GetString());
        await RunDotnet("msbuild", project, "-t:RequireSunderPackageApi;UnrequireSunderPackageApi", "-nr:false", "-nologo",
            "-p:SunderPackageId=" + DependencyId, "-p:SunderContractId=" + ContractId);
        Assert.Empty(SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PackageImports[DependencyId].RequiredContracts);
        var before = File.ReadAllBytes(LockPath);
        var failure = await RunDotnet(false, "msbuild", project, "-t:RemoveSunderPackageApi", "-nr:false", "-nologo",
            "-p:SunderPackageId=" + DependencyId, "-p:SunderPackageApiPresence=");
        Assert.Contains("Supplied command values must be nonempty", failure, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(LockPath));
    }

    private ManageSunderPackageApiTask CreatePackageApiTask(string operation)
    {
        var generation = CreateGeneration();
        return new()
        {
            BuildEngine = new BuildEngine(), Operation = operation, ProjectDirectory = _root, ContractCacheRoot = _cache,
            PackageId = DependencyId, PackageVersion = "1.0.0", SourceFiles = generation.SourceFiles, ReferencePaths = generation.ReferencePaths,
            OutputType = "Exe", ProjectIdentity = "ImportedFixture", Configuration = "Debug", TargetFramework = "net10.0", IntermediateOutputPath = generation.IntermediateOutputPath,
        };
    }

    [Theory]
    [InlineData(false, "package-api.required-contract-removed:")]
    [InlineData(true, "package-api.provider-contract-removed:")]
    public async Task PackageApiCommands_UpdateCannotRemoveReferencedInventoryMembers(bool provider, string diagnostic)
    {
        var document = ReadJson(LockPath);
        if (provider)
        {
            document["packageImports"]![DependencyId]!["requiredContracts"] = new JsonArray();
            document["providerImports"]!["minimalProvider"] = new JsonObject { ["packageId"] = DependencyId, ["contractId"] = ContractId };
            WriteJson(LockPath, document);
        }
        using var ordinary = CommandHandler();
        using var ordinaryHttp = new HttpClient(ordinary);
        using var handler = new RestoreHandler(request =>
        {
            var response = ordinaryHttp.SendAsync(new HttpRequestMessage(request.Method, request.RequestUri)).GetAwaiter().GetResult();
            if (request.RequestUri!.AbsolutePath.EndsWith("/canonical", StringComparison.Ordinal))
            {
                response.Dispose();
                response = new(HttpStatusCode.OK) { RequestMessage = request,
                    Content = new ByteArrayContent(CanonicalCandidateBytes(Uri.UnescapeDataString(request.RequestUri.Segments[^2].TrimEnd('/')), "example.other.contract")) };
                response.Content.Headers.ContentType = new("application/octet-stream");
            }
            return response;
        });
        using var http = new HttpClient(handler);
        var before = File.ReadAllBytes(LockPath);
        var cache = Path.Combine(_root, "command-cache");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new SunderRpcPackageApiCommands(new(http)).ExecuteAsync(LockPath, cache, new("Update", DependencyId)));
        Assert.StartsWith(diagnostic, error.Message);
        Assert.Equal(before, File.ReadAllBytes(LockPath));
        Assert.False(Directory.Exists(cache));
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task PackageApiCommands_GlobalUpdateLastMemberFailurePreservesEverySelection()
    {
        var document = ReadJson(LockPath);
        var reexport = document["packageArtifacts"]![DependencyId]!.DeepClone();
        reexport["selectedPackageContentIdentity"] = new string('2', 64);
        reexport["contracts"]![ContractId]!["artifactSource"] = "imported";
        reexport["publicInventorySha256"] = InventorySha256("example.zreexport", reexport);
        document["packageArtifacts"]!["example.zreexport"] = reexport;
        document["packageImports"]!["example.zreexport"] = new JsonObject
        { ["localName"] = "reexport", ["presence"] = "optional", ["requiredContracts"] = new JsonArray(), ["consumerRoles"] = new JsonArray("runtime") };
        WriteJson(LockPath, document);
        var before = File.ReadAllBytes(LockPath);
        using var ordinary = new HttpClient(CommandHandler());
        using var handler = new RestoreHandler(request => request.RequestUri!.AbsolutePath.Contains("example.zreexport", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request }
            : ordinary.SendAsync(new HttpRequestMessage(request.Method, request.RequestUri)).GetAwaiter().GetResult());
        using var http = new HttpClient(handler);
        var cache = Path.Combine(_root, "command-cache");
        await Assert.ThrowsAsync<InvalidDataException>(() => new SunderRpcPackageApiCommands(new(http)).ExecuteAsync(LockPath, cache, new("Update")));
        Assert.Equal(3, handler.Requests);
        Assert.Equal(before, File.ReadAllBytes(LockPath));
        Assert.False(Directory.Exists(cache));
        Assert.Empty(Directory.GetFiles(_root, "*.stage-*"));
    }

    [Fact]
    public async Task PackageApiCommands_CandidateChangeBeforeCommitPreservesTheLock()
    {
        var candidate = await CreateLockedCandidateAsync();
        var before = File.ReadAllBytes(LockPath);
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException("Local selection attempted HTTP.")));
        await Assert.ThrowsAsync<InvalidDataException>(() => new SunderRpcPackageApiCommands(new(http)).ExecuteAsync(
            LockPath, _cache, new("Update", DependencyId, Candidate: candidate, Presence: "optional"),
            (_, _, _) => File.WriteAllBytes(candidate, CanonicalCandidateBytes("1.2.4"))));
        Assert.Equal(before, File.ReadAllBytes(LockPath));
        Assert.Empty(Directory.GetFiles(_root, "*.stage-*"));
    }

    [Fact]
    public async Task PackageApiCommands_LostSelectionLeaseNeverPublishes()
    {
        var before = File.ReadAllBytes(LockPath);
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException("Offline command attempted HTTP.")));
        await Assert.ThrowsAsync<IOException>(() => new SunderRpcPackageApiCommands(new(http)).ExecuteAsync(
            LockPath, _cache, new("Unrequire", DependencyId, ContractId),
            (_, _, _) => Directory.Delete(LockPath + GeneratedOutputLock.LockSuffix, true)));
        Assert.Equal(before, File.ReadAllBytes(LockPath));
        Assert.Empty(Directory.GetFiles(_root, "*.stage-*"));
    }

    [Fact]
    public async Task PackageApiCommands_ConcurrentSourceLockEditCannotBeOverwritten()
    {
        using var http = new HttpClient(CommandHandler());
        var changed = ReadJson(LockPath);
        changed["packageImports"]![DependencyId]!["presence"] = "optional";
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new SunderRpcPackageApiCommands(new(http)).ExecuteAsync(
            LockPath, Path.Combine(_root, "command-cache"), new("Update", DependencyId), (_, _, _) => WriteJson(LockPath, changed)));
        Assert.StartsWith("rpc-authoring.evidence-mismatch:", error.Message);
        Assert.True(JsonNode.DeepEquals(changed, ReadJson(LockPath)));
        Assert.Empty(Directory.GetFiles(_root, "*.stage-*"));
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task PackageApiCommands_MsbuildCandidateAddProviderIntentAndOfflineBuildUseTheSameLock()
    {
        var candidate = await CreateLockedCandidateAsync();
        var expected = ReadJson(LockPath);
        expected["packageImports"]![DependencyId]!["requiredContracts"] = new JsonArray();
        expected["packageImports"]![DependencyId]!["consumerRoles"] = new JsonArray("app", "runtime");
        expected["packageArtifacts"]![DependencyId]!.AsObject().Remove("registryOrigin");
        var empty = new JsonObject
        {
            ["lockVersion"] = 1, ["packageArtifacts"] = new JsonObject(), ["packageImports"] = new JsonObject(),
            ["providerImports"] = new JsonObject(), ["platformImports"] = new JsonObject(),
        };
        WriteJson(LockPath, empty);
        var project = CreateMsbuildImportProject(Path.Combine(_root, "command-cache"));
        var feed = Path.Combine(_root, "empty-feed");
        Directory.CreateDirectory(feed);
        await RunDotnet("restore", project, "--source", feed, "--disable-parallel", "--nologo");
        var sourceBefore = File.ReadAllBytes(Path.Combine(_root, "Consumer.cs"));
        await RunDotnet("msbuild", project, "-t:AddSunderPackageApi", "-nr:false", "-nologo", "-p:SunderPackageId=" + DependencyId,
            "-p:SunderPackageVersionRange=>=1.0.0 <2.0.0", "-p:SunderPackageCandidatePath=" + candidate);
        Assert.True(JsonNode.DeepEquals(expected, ReadJson(LockPath)));
        Assert.Equal(sourceBefore, File.ReadAllBytes(Path.Combine(_root, "Consumer.cs")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "obj"), "authoring-receipt.v1.json", SearchOption.AllDirectories));
        var selected = File.ReadAllBytes(LockPath);
        await RunDotnet("msbuild", project, "-t:ImplementSunderPackageApi", "-nr:false", "-nologo", "-p:SunderPackageId=" + DependencyId,
            "-p:SunderContractId=" + ContractId, "-p:SunderPackageCandidatePath=" + candidate);
        Assert.Single(SunderRpcAuthoringDocuments.ReadContractLock(LockPath).ProviderImports);
        var missing = await RunDotnet(false, "build", project, "--no-restore", "--disable-build-servers", "--nologo");
        Assert.Contains("rpc-authoring.handler-incomplete", missing, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_root, "Provider.cs"), ProviderSource);
        await RunDotnet("build", project, "--no-restore", "--disable-build-servers", "--nologo");
        File.Delete(Path.Combine(_root, "Provider.cs"));
        await RunDotnet("msbuild", project, "-t:UnimplementSunderPackageApi", "-nr:false", "-nologo", "-p:SunderPackageId=" + DependencyId,
            "-p:SunderContractId=" + ContractId);
        Assert.Equal(selected, File.ReadAllBytes(LockPath));
        var before = File.ReadAllBytes(LockPath);
        var failure = await RunDotnet(false, "msbuild", project, "-t:UpdateSunderPackageApi", "-nr:false", "-nologo", "-p:SunderPackageId=" + DependencyId,
            "-p:SunderPackageCandidatePath=" + candidate, "-p:SunderPackageIncludePrerelease=");
        Assert.Contains("SunderPackageIncludePrerelease must be absent or exactly true/false", failure, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(LockPath));
    }

    private RestoreHandler CommandHandler() => new(request =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal))
        {
            var versions = new[] { "1.2.3", "1.9.0", "1.10.0-rc.1", "2.0.0", "1.99.0" };
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(JsonSerializer.Serialize(versions.Select(version => new { version, isYanked = version == "1.99.0" })), System.Text.Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("X-Total-Count", versions.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            response.Headers.Add("X-Skip", "0");
            response.Headers.Add("X-Take", "100");
            return response;
        }
        var original = SunderRpcAuthoringDocuments.ReadContractLock(Fixture("authoring/conformance/valid/package-api-contract-lock.json")).PackageArtifacts[DependencyId];
        if (request.RequestUri.AbsolutePath.EndsWith("/canonical", StringComparison.Ordinal))
        {
            var version = Uri.UnescapeDataString(request.RequestUri.Segments[^2].TrimEnd('/'));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(CanonicalCandidateBytes(version)) };
            response.Content.Headers.ContentType = new("application/octet-stream");
            return response;
        }
        var inventory = JsonNode.Parse(SunderRpcAuthoringDocuments.ValidatePackagePublicInventory(original).ToCanonicalUtf8())!;
        if (request.RequestUri.AbsolutePath.EndsWith("/public-api", StringComparison.Ordinal))
        {
            var version = Uri.UnescapeDataString(request.RequestUri.Segments[^2].TrimEnd('/'));
            inventory["packageVersion"] = version;
            inventory["contracts"]![0]!["selectedAuthorityRelease"] = version;
        }
        return RestoreResponse(request, SunderPackagePublicApiInventory.Parse(JsonSerializer.SerializeToUtf8Bytes(inventory, JsonOptions)));
    });

    private byte[] CanonicalCandidateBytes(string version, string? contractId = null, int contractCount = 1, SunderRpcMethodPolicies? methodPolicies = null,
        string packageId = DependencyId, string? contractVersion = null)
    {
        var directory = Directory.CreateTempSubdirectory("sunder-authoring-http-candidate-").FullName;
        try
        {
            var root = Path.Combine(directory, "package");
            Directory.CreateDirectory(Path.Combine(root, "manifest"));
            Directory.CreateDirectory(Path.Combine(root, "payload/shared/contracts"));
            var descriptors = Enumerable.Range(0, contractCount).Select(index =>
            {
                var json = JsonNode.Parse(_descriptor.GetCanonicalUtf8())!;
                json["contractId"] = contractCount == 1 ? contractId ?? _descriptor.ContractId : $"example.rpc.v1.contract{index:D3}";
                if (contractVersion is not null) json["version"] = contractVersion;
                if (contractCount > 1)
                {
                    var services = json["services"]!.AsObject(); var service = services["example"]!;
                    services.Remove("example"); services[$"service{index:D3}"] = service;
                }
                return Sunder.Sdk.Rpc.SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(json));
            }).ToArray();
            string DescriptorPath(int index) => contractCount == 1 ? "contracts/api.json" : $"contracts/api{index:D3}.json";
            var manifest = new SunderPackageManifest
            {
                ArchiveFormatVersion = 1, ManifestVersion = 1, Id = packageId, Name = "Dependency", Version = version, Targets = [], DependsOn = [],
                ContractBundles = descriptors.Select((descriptor, index) => new SunderPackageContractBundleManifest
                {
                    Source = "owned", AuthorityKind = "package", AuthorityId = packageId, ContractId = descriptor.ContractId,
                    Version = descriptor.Version, DescriptorPath = DescriptorPath(index), DescriptorSha256 = descriptor.Sha256,
                    AbiSha256 = descriptor.AbiSha256, Exposure = "public",
                }).ToArray(),
                UsesContracts = [], Provides = [], PackageArtifactImports = [], PackageApiImports = [], ProviderImports = [], PlatformImports = [],
            };
            File.WriteAllBytes(Path.Combine(root, SunderPackageFormat.ManifestPath), JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
            for (var index = 0; index < descriptors.Length; index++)
                File.WriteAllBytes(Path.Combine(root, SunderPackageFormat.SharedPayloadRoot + DescriptorPath(index)), descriptors[index].GetCanonicalUtf8());
            var projection = new SunderRpcModelProjectionDocument(1, packageId, version,
                descriptors.Select(descriptor => new SunderRpcModelProjectionContract(descriptor.ContractId, descriptor.Version,
                    descriptor.Sha256, descriptor.AbiSha256, SunderRpcModelProjection.Empty, methodPolicies)).ToArray());
            File.WriteAllBytes(Path.Combine(root, SunderPackageFormat.RpcModelProjectionPath), projection.ToCanonicalUtf8());
            PackageContentIndexer.Write(root);
            var path = Path.Combine(directory, "candidate.sunderpkg");
            SunderArchive.WriteDeterministicAsync(root, path).GetAwaiter().GetResult();
            return File.ReadAllBytes(path);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
