using System.Text.Json.Nodes;
using Sunder.Package.Build.Rpc.Authoring;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcImportedBuildTests
{
    [Fact]
    public async Task PlatformCommands_LocalCatalogSelectsAndUpdatesWithoutRegistry()
    {
        var fixture = PlatformFixture();
        File.Copy(Fixture("authoring/conformance/valid/minimal-contract-lock.json"), LockPath, overwrite: true);
        File.Delete(Path.Combine(_root, "Consumer.cs"));
        var ownedPath = Path.Combine(_root, SunderRpcAuthoringDocuments.OwnedContractsFileName);
        File.Delete(ownedPath);
        using var handler = new RestoreHandler(_ => throw new InvalidOperationException("Local catalog selection attempted HTTP."));
        using var http = new HttpClient(handler);
        var commands = new SunderRpcPackageApiCommands(new(http));
        var request = new SunderRpcPackageApiRequest("PlatformImplement", ContractId: "sunder.stack.contributor", PlatformCatalog: fixture.Path);
        var added = await commands.ExecuteAsync(LockPath, _cache, request,
            (before, _, stage) => CreatePackageApiTask(request.Operation).ValidateSource(before, stage, _cache, request));
        Assert.True(added.Changed);
        Assert.False(File.Exists(ownedPath));
        Assert.Null(Assert.Single(added.ContractLock.PlatformImports).Value.RegistryOrigin);
        Assert.DoesNotContain("registryOrigin", File.ReadAllText(LockPath), StringComparison.Ordinal);
        var pinned = File.ReadAllBytes(LockPath);
        Assert.False((await commands.ExecuteAsync(LockPath, _cache, request)).Changed);
        Assert.False((await commands.ExecuteAsync(LockPath, _cache,
            new("PlatformUpdate", ContractId: request.ContractId, PlatformCatalog: fixture.Path))).Changed);
        var cold = Path.Combine(_root, "local-platform-cold-cache");
        Assert.Equal(1, await new SunderRpcContractRestore(http).RestoreAsync(LockPath, cold, [], platformCatalog: fixture.Path));
        Assert.Single(SunderRpcPlatformCatalogCache.Resolve(added.ContractLock, cold));
        Assert.Equal(pinned, File.ReadAllBytes(LockPath));
        Assert.Equal(0, handler.Requests);
        var missing = await Assert.ThrowsAsync<InvalidDataException>(() => commands.ExecuteAsync(LockPath, _cache, new("PlatformUpdate")));
        Assert.StartsWith("platform-api.local-input-required:", missing.Message);
        var globalOrigin = await Assert.ThrowsAsync<InvalidDataException>(() => commands.ExecuteAsync(LockPath, _cache,
            new("PlatformUpdate", RegistryOrigin: "https://registry.example/")));
        Assert.Contains("requires a specific platform contract", globalOrigin.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(pinned, File.ReadAllBytes(LockPath));
    }

    [Fact]
    public async Task PlatformCommands_SelectStageSourceAndRemoveOnlyAfterBindingsAreRemoved()
    {
        var fixture = PlatformFixture();
        File.Copy(Fixture("authoring/conformance/valid/minimal-contract-lock.json"), LockPath, overwrite: true);
        File.Delete(Path.Combine(_root, "Consumer.cs"));
        using var handler = new RestoreHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/resolve", StringComparison.Ordinal))
                Assert.Equal("?versionRange=" + Uri.EscapeDataString(">=1.1.0 <1.2.0"), request.RequestUri.Query);
            return PlatformResponse(request, fixture);
        });
        using var http = new HttpClient(handler);
        var commands = new SunderRpcPackageApiCommands(new(http));
        Task<SunderRpcPackageApiResult> Run(SunderRpcPackageApiRequest request) => commands.ExecuteAsync(LockPath, _cache, request,
            (before, _, stage) => CreatePackageApiTask(request.Operation).ValidateSource(before, stage, _cache, request));
        var added = await Run(new("PlatformImplement", ContractId: "sunder.stack.contributor", RegistryOrigin: "https://registry.example/"));
        Assert.True(added.Changed); var imported = Assert.Single(added.ContractLock.PlatformImports).Value;
        Assert.Equal("stackContributor", imported.Key); Assert.Equal(">=1.1.0 <1.2.0", imported.AuthorityReleaseRange);
        Assert.Empty(added.ContractLock.PackageArtifacts); Assert.Empty(added.ContractLock.PackageImports);
        var before = File.ReadAllBytes(LockPath); var time = File.GetLastWriteTimeUtc(LockPath); var requests = handler.Requests;
        Assert.False((await Run(new("PlatformImplement", ContractId: imported.ContractId))).Changed);
        Assert.Equal(requests, handler.Requests); Assert.Equal(time, File.GetLastWriteTimeUtc(LockPath));
        Assert.False(CreateGeneration().Execute());
        File.WriteAllText(Path.Combine(_root, "Provider.cs"), PlatformProviderSource.Replace("IMinimalProviderService", "IStackContributorService", StringComparison.Ordinal)
            .Replace("CExample_Rpc_V1_Minimal", "CSunder_Stack_Contributor", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(_root, "Program.cs"), WorkerSource.Replace("new SunderWorkerOptions([])",
            "new SunderWorkerOptions([new LocalProvider().CreateSunderRpcProviderRegistration()])", StringComparison.Ordinal));
        Assert.False((await Run(new("PlatformUpdate", ContractId: imported.ContractId))).Changed);
        Assert.False((await Run(new("Update"))).Changed); // global package update also covers platform-only locks
        var live = await Assert.ThrowsAsync<InvalidDataException>(() => Run(new("PlatformUnimplement", ContractId: imported.ContractId)));
        Assert.Contains("remove-still-referenced", live.Message, StringComparison.Ordinal); Assert.Equal(before, File.ReadAllBytes(LockPath));
        File.Delete(Path.Combine(_root, "Provider.cs")); File.WriteAllText(Path.Combine(_root, "Program.cs"), WorkerSource);
        Assert.True((await Run(new("PlatformUnimplement", ContractId: imported.ContractId))).Changed);
        Assert.Empty(SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PlatformImports);
        Assert.True(File.Exists(SunderRpcPlatformCatalogCache.CatalogPath(_cache, imported.SelectedAuthorityContentIdentity)));
    }

    [Theory]
    [InlineData("range")]
    [InlineData("prerelease")]
    [InlineData("candidate")]
    [InlineData("origin-update")]
    [InlineData("presence")]
    public async Task PlatformCommands_RejectAuthoredSelectionOverridesBeforeHttp(string invalid)
    {
        var fixture = PlatformFixture();
        using var handler = new RestoreHandler(_ => throw new InvalidOperationException("Unexpected HTTP.")); using var http = new HttpClient(handler);
        var request = invalid switch
        {
            "range" => new SunderRpcPackageApiRequest("PlatformImplement", ContractId: "sunder.stack.contributor", VersionRange: ">=1.0.0"),
            "prerelease" => new("PlatformImplement", ContractId: "sunder.stack.contributor", IncludePrerelease: false),
            "candidate" => new("PlatformImplement", ContractId: "sunder.stack.contributor", Candidate: "candidate.sunderpkg"),
            "presence" => new("PlatformImplement", ContractId: "sunder.stack.contributor", Presence: "optional"),
            _ => new("PlatformUpdate", ContractId: "sunder.stack.contributor", RegistryOrigin: "https://registry.example/"),
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => new SunderRpcPackageApiCommands(new(http)).ExecuteAsync(LockPath, _cache, request));
        Assert.Equal(0, handler.Requests); Assert.Equal(fixture.Lock.ExactBytes, File.ReadAllBytes(LockPath));
    }

    [Fact]
    public async Task PlatformCommands_RejectRevokedResolutionAndFailedSourceBeforeSelectionCommit()
    {
        var fixture = PlatformFixture(); var revoked = true;
        using var handler = new RestoreHandler(request =>
        {
            var response = PlatformResponse(request, fixture);
            if (revoked && request.RequestUri!.AbsolutePath.EndsWith("/resolve", StringComparison.Ordinal))
            {
                var value = JsonNode.Parse(response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult())!; value["lifecycleState"] = "Revoked";
                response.Content = new ByteArrayContent(Compact(value)); response.Content.Headers.ContentType = new("application/json");
            }
            return response;
        });
        using var http = new HttpClient(handler); var commands = new SunderRpcPackageApiCommands(new(http));
        await Assert.ThrowsAsync<InvalidDataException>(() => commands.ExecuteAsync(LockPath, _cache, new("PlatformUpdate")));
        Assert.Equal(fixture.Lock.ExactBytes, File.ReadAllBytes(LockPath)); Assert.Equal(1, handler.Requests);
        revoked = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => commands.ExecuteAsync(LockPath, _cache, new("PlatformUpdate"), (_, _, _) => throw new InvalidDataException("staged source")));
        Assert.Equal(fixture.Lock.ExactBytes, File.ReadAllBytes(LockPath));
    }
}
