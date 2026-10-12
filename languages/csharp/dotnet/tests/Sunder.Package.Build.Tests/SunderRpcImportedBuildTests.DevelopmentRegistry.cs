using System.Net;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcImportedBuildTests
{
    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://localhost:43127/")]
    public async Task DevelopmentRegistry_ExactRestoreAndOfflineGenerationRequireExplicitOptIn(string origin)
    {
        _ = await CreateLockedCandidateAsync();
        SetDevelopmentOrigin(origin);
        var locked = SunderRpcAuthoringDocuments.ReadContractLock(LockPath, true);
        var inventory = SunderRpcAuthoringDocuments.ValidatePackagePublicInventory(locked.PackageArtifacts[DependencyId]);
        using var handler = new RestoreHandler(request =>
        {
            Assert.StartsWith(origin, request.RequestUri!.AbsoluteUri);
            return RestoreResponse(request, inventory);
        });
        using var http = new HttpClient(handler); var cache = Path.Combine(_root, "development-cache"); var before = File.ReadAllBytes(LockPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SunderRpcContractRestore(http).RestoreAsync(LockPath, cache, []));
        Assert.Equal(0, handler.Requests); Assert.False(Directory.Exists(cache));
        var restore = new SunderRpcContractRestore(http, allowDevelopmentRegistryOrigin: true);
        Assert.Equal(1, await restore.RestoreAsync(LockPath, cache, []));
        var denied = CreateGeneration(); denied.ContractCacheRoot = cache; Assert.False(denied.Execute());
        var allowed = CreateGeneration(); allowed.ContractCacheRoot = cache; allowed.AllowDevelopmentRegistryOrigin = true; AssertSuccess(allowed);
        var snapshot = Snapshot(cache); Assert.Equal(0, await restore.RestoreAsync(LockPath, cache, [])); Assert.Equal(snapshot, Snapshot(cache));
        Assert.Equal(before, File.ReadAllBytes(LockPath)); Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("http://127.0.0.1:43127/")]
    [InlineData("http://[::1]:43127/")]
    [InlineData("http://localhost.example/")]
    [InlineData("http://LOCALHOST:43127/")]
    [InlineData("http://localhost:80/")]
    [InlineData("http://localhost:043127/")]
    [InlineData("http://localhost:0/")]
    [InlineData("http://localhost:65536/")]
    [InlineData("http://user@localhost/")]
    [InlineData("http://localhost/path/")]
    [InlineData("http://localhost/?query=1")]
    [InlineData("http://localhost/#fragment")]
    public async Task DevelopmentRegistry_RejectsNoncanonicalOriginsBeforeNetworkOrCache(string origin)
    {
        SetDevelopmentOrigin(origin); var before = File.ReadAllBytes(LockPath);
        using var handler = new RestoreHandler(_ => throw new InvalidOperationException("Invalid origins must not reach HTTP."));
        using var http = new HttpClient(handler); var cache = Path.Combine(_root, "development-cache");
        var restore = new SunderRpcContractRestore(http, allowDevelopmentRegistryOrigin: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => restore.RestoreAsync(LockPath, cache, []));
        await Assert.ThrowsAsync<InvalidDataException>(() => restore.SelectArtifactAsync(DependencyId, ">=1.0.0 <2.0.0", false, origin, null, default));
        Assert.Equal(0, handler.Requests); Assert.Equal(before, File.ReadAllBytes(LockPath)); Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public async Task DevelopmentRegistry_SelectionUpdateAndReadinessKeepLockedOriginAndOptIn()
    {
        using var handler = CommandHandler(); using var http = new HttpClient(handler);
        File.Copy(Fixture("authoring/conformance/valid/minimal-contract-lock.json"), LockPath, overwrite: true);
        var request = new SunderRpcPackageApiRequest("Add", DependencyId, VersionRange: ">=1.0.0 <2.0.0", RegistryOrigin: "http://localhost:43127/");
        var denied = new SunderRpcPackageApiCommands(new(http));
        await Assert.ThrowsAsync<InvalidDataException>(() => denied.ExecuteAsync(LockPath, _cache, request)); Assert.Equal(0, handler.Requests);
        var commands = new SunderRpcPackageApiCommands(new(http, allowDevelopmentRegistryOrigin: true));
        var selected = await commands.ExecuteAsync(LockPath, _cache, request);
        Assert.Equal("http://localhost:43127/", selected.ContractLock.PackageArtifacts[DependencyId].RegistryOrigin);
        Assert.False((await commands.ExecuteAsync(LockPath, _cache, new("Update", DependencyId))).Changed);
        var before = File.ReadAllBytes(LockPath); var count = handler.Requests;
        await Assert.ThrowsAsync<InvalidDataException>(() => denied.ExecuteAsync(LockPath, _cache, new("List")));
        await Assert.ThrowsAsync<InvalidDataException>(() => commands.ExecuteAsync(LockPath, _cache, new("Update", DependencyId, RegistryOrigin: request.RegistryOrigin)));
        Assert.Equal(count, handler.Requests); Assert.Equal(before, File.ReadAllBytes(LockPath));
        Assert.True((await commands.ExecuteAsync(LockPath, _cache, new("Require", DependencyId, ContractId))).Changed);
        Assert.True((await commands.ExecuteAsync(LockPath, _cache, new("Unrequire", DependencyId, ContractId))).Changed);
        Assert.Equal(count, handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevelopmentRegistry_RejectsRedirectAndDifferentResponseOrigin(bool differentOrigin)
    {
        _ = await CreateLockedCandidateAsync();
        SetDevelopmentOrigin("http://localhost:43127/");
        var locked = SunderRpcAuthoringDocuments.ReadContractLock(LockPath, true);
        var inventory = SunderRpcAuthoringDocuments.ValidatePackagePublicInventory(locked.PackageArtifacts[DependencyId]);
        using var handler = new RestoreHandler(request =>
        {
            var response = RestoreResponse(request, inventory);
            if (differentOrigin) response.RequestMessage = new(HttpMethod.Get, "http://localhost:43128/changed");
            else { response.StatusCode = HttpStatusCode.Redirect; response.Headers.Location = new Uri("http://localhost:43128/changed"); }
            return response;
        });
        using var http = new HttpClient(handler); var cache = Path.Combine(_root, "development-cache"); var before = File.ReadAllBytes(LockPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SunderRpcContractRestore(http, allowDevelopmentRegistryOrigin: true).RestoreAsync(LockPath, cache, []));
        Assert.Equal(1, handler.Requests); Assert.False(Directory.Exists(cache)); Assert.Equal(before, File.ReadAllBytes(LockPath));
    }

    [Fact]
    public async Task DevelopmentRegistry_PlatformOriginsRequireExplicitTransportModeWithoutSigningInputs()
    {
        var fixture = PlatformFixture(); var value = ReadJson(LockPath);
        value["platformImports"]!["stackContributor"]!["registryOrigin"] = "http://localhost:43127/"; WriteJson(LockPath, value);
        var locked = SunderRpcAuthoringDocuments.ReadContractLock(LockPath, true);
        fixture = fixture with { Lock = locked, Requirement = Assert.Single(SunderRpcPlatformCatalogCache.Requirements(locked)) };
        using var handler = new RestoreHandler(request => PlatformResponse(request, fixture)); using var http = new HttpClient(handler);
        var cache = Path.Combine(_root, "development-platform-cache");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new SunderRpcContractRestore(http).RestoreAsync(LockPath, cache, []));
        Assert.Contains("canonical", error.Message); Assert.Equal(0, handler.Requests);
        var restore = new SunderRpcContractRestore(http, allowDevelopmentRegistryOrigin: true);
        Assert.Equal(1, await restore.RestoreAsync(LockPath, cache, []));
        Assert.Single(SunderRpcPlatformCatalogCache.Resolve(locked, cache));
        var selected = await restore.SelectPlatformAsync("sunder.stack.contributor", fixture.Requirement.RegistryOrigin, default);
        Assert.Equal(fixture.Requirement.RegistryOrigin, selected["registryOrigin"]!.GetValue<string>());
        Assert.Equal(fixture.Requirement.ContentIdentity, selected["selectedAuthorityContentIdentity"]!.GetValue<string>());
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task DevelopmentRegistry_MsbuildCandidateSelectRestoreBuildAndPackPropagateExplicitMode()
    {
        var archive = await CreateLockedCandidateAsync();
        File.Copy(Fixture("authoring/conformance/valid/minimal-contract-lock.json"), LockPath, overwrite: true);
        var cache = Path.Combine(_root, "development-cache"); var project = CreateMsbuildImportProject(cache);
        var feed = Path.Combine(_root, "empty-feed"); Directory.CreateDirectory(feed);
        await RunDotnet("restore", project, "--source", feed, "--disable-parallel", "--nologo");
        var arguments = new[] { "msbuild", project, "-t:AddSunderPackageApi", "-nr:false", "-nologo", "-p:SunderPackageId=" + DependencyId,
            "-p:SunderPackageVersionRange=>=1.0.0 <2.0.0", "-p:SunderRegistryOrigin=http://localhost:43127/", "-p:SunderPackageCandidatePath=" + archive };
        Assert.Contains("canonical", await RunDotnet(false, arguments));
        await RunDotnet(arguments.Append("-p:SunderAllowDevelopmentRegistryOrigin=true").ToArray());
        var before = File.ReadAllBytes(LockPath);
        Assert.Contains("not canonical", await RunDotnet(false, "build", project, "--no-restore", "--nologo", "--disable-build-servers"));
        await RunDotnet("build", project, "--no-restore", "--nologo", "--disable-build-servers", "-p:SunderAllowDevelopmentRegistryOrigin=true");
        await RunDotnet("msbuild", project, "-t:RestoreSunderContracts", "-nr:false", "-nologo", "-p:SunderAllowDevelopmentRegistryOrigin=true", "-p:SunderPackageCandidatePath=" + archive);
        await RunDotnet("msbuild", project, "-t:CheckSunderContracts;FreezeSunderContracts;PackSunderPackage", "-nr:false", "-nologo", "-p:SunderAllowDevelopmentRegistryOrigin=true");
        Assert.Equal(before, File.ReadAllBytes(LockPath)); Assert.NotEmpty(Directory.GetFiles(Path.Combine(_root, "bin"), "*.sunderpkg", SearchOption.AllDirectories));
    }

    private void SetDevelopmentOrigin(string origin)
    {
        var value = ReadJson(LockPath); value["packageArtifacts"]![DependencyId]!["registryOrigin"] = origin; WriteJson(LockPath, value);
    }
}
