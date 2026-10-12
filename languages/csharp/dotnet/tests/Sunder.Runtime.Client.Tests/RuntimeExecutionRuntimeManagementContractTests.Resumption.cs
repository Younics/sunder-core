using System.Net.Http.Json;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Client.Tests;

public sealed partial class RuntimeExecutionRuntimeManagementContractTests
{
    [Fact]
    public async Task PackageResumption_TransmitsLeaseAndAdoptionProofWithoutPuttingHandlesInUrls()
    {
        var id = Guid.NewGuid().ToString("N");
        var handle = new RuntimePackageUploadRetentionHandle(id, Guid.NewGuid().ToString("N"), Hash, 3);
        var retention = new RuntimePackageUploadRetention(handle, DateTimeOffset.UtcNow.AddMinutes(30), DateTimeOffset.UtcNow.AddHours(2));
        var expectation = new RuntimePackageRuntimePrerequisiteExpectation(Hash, "osx-arm64");
        var stage = new PackageStoreStageRequest([new(PackageStoreMutationKind.Install, UploadId: id) { UploadRetention = handle }])
            { RuntimePrerequisiteExpectation = expectation };
        var sources = new List<RuntimeRegistryPackageTargetRequest> { new("runtime", "osx-arm64") };
        var adoption = new RuntimeRegistrySourceAdoptionRequest("example.package", "https://registry.example/", Version: "1.0.0", DesiredTargets: sources,
            DryRun: true, ExpectedResolutionFingerprint: Hash) { RuntimePrerequisiteExpectation = expectation };
        var preview = new RuntimePackageRuntimePrerequisiteRequest { Adoption = adoption };
        sources.Clear();
        Assert.Single(preview.Adoption!.DesiredTargets!);
        var requests = new List<string>();
        using var handler = new Handler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/handshake") return Reply(Handshake(true) with
                { SupportedFeatures = [.. Handshake(true).SupportedFeatures, RuntimeProtocolFeatures.PackageInstallResumptionV1] });
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Empty(request.RequestUri.Query);
            Assert.Equal("Bearer secret", request.Headers.Authorization?.ToString());
            requests.Add(path);
            if (path == RuntimeApiRoutes.PackageUploadRetention)
            {
                Assert.Equal(new RuntimePackageUploadRetentionRequest(id, Hash, 3), await request.Content!.ReadFromJsonAsync<RuntimePackageUploadRetentionRequest>());
                return Reply(new RuntimePackageUploadRetentionResult(true, null, "Retained.", retention));
            }
            if (path.StartsWith(RuntimeApiRoutes.PackageUploadRetention + "/", StringComparison.Ordinal))
            {
                Assert.Equal(handle, await request.Content!.ReadFromJsonAsync<RuntimePackageUploadRetentionHandle>());
                return Reply(new RuntimePackageUploadRetentionResult(true, null, "OK.", path.EndsWith("/release", StringComparison.Ordinal) ? null : retention));
            }
            if (path == RuntimeApiRoutes.PackageRuntimePrerequisites)
            {
                var body = (await request.Content!.ReadFromJsonAsync<RuntimePackageRuntimePrerequisiteRequest>())!;
                if (body.Local is not null) Assert.Equal(handle, Assert.Single(body.Local.Mutations).UploadRetention);
                else Assert.Equal(expectation, body.Adoption!.RuntimePrerequisiteExpectation);
                return Reply(new RuntimePackageRuntimePrerequisitePlan(true, true, null, "Ready to revalidate.", Hash, "osx-arm64", [], []));
            }
            if (path == "/api/v1/packages/store/stage")
            {
                var body = (await request.Content!.ReadFromJsonAsync<PackageStoreStageRequest>())!;
                Assert.Equal(handle, Assert.Single(body.Mutations).UploadRetention);
                Assert.Equal(expectation, body.RuntimePrerequisiteExpectation);
                return Reply(PackageStoreStageResult.Failed("No stage in this transport test."));
            }
            Assert.Equal("/api/v1/registry/packages/adopt-source", path);
            var adopt = (await request.Content!.ReadFromJsonAsync<RuntimeRegistrySourceAdoptionRequest>())!;
            Assert.Equal(expectation, adopt.RuntimePrerequisiteExpectation);
            Assert.Equal(Hash, adopt.ExpectedResolutionFingerprint);
            return Reply(new RuntimeRegistryPackageChangeResult(false, RuntimeRegistryErrorCode.StalePlan, "Re-preview.", false, false, [], [], [], []));
        });
        using var client = new RuntimeManagementClient(() => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"), handler);
        Assert.True((await client.RetainPackageUploadAsync(new(id, Hash, 3))).Success);
        await client.RenewPackageUploadRetentionAsync(handle);
        await client.GetPackageUploadRetentionAsync(handle);
        await client.PreviewPackageRuntimePrerequisitesAsync(new(Local: stage));
        await client.StagePackageStoreChangesAsync(stage);
        await client.PreviewPackageRuntimePrerequisitesAsync(preview);
        await client.AdoptRegistryPackageSourceAsync(preview.Adoption with { DryRun = false, Confirm = true });
        await client.ReleasePackageUploadRetentionAsync(handle);
        Assert.Equal(8, requests.Count);
        Assert.Contains("/api/v1/uploads/packages/retention/renew", requests);
        Assert.Contains("/api/v1/uploads/packages/retention/status", requests);
        Assert.Contains("/api/v1/uploads/packages/retention/release", requests);
    }

    [Fact]
    public async Task PackageResumption_NeverSendsProofToAnOlderHostThatWouldIgnoreIt()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("/api/handshake", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Reply(Handshake(true)));
        });
        using var client = new RuntimeManagementClient(() => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"), handler);
        var handle = new RuntimePackageUploadRetentionHandle(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Hash, 3);
        await Assert.ThrowsAsync<RuntimeProtocolException>(() => client.RetainPackageUploadAsync(new(handle.UploadId, Hash, 3)));
        await Assert.ThrowsAsync<RuntimeProtocolException>(() => client.StagePackageStoreChangesAsync(new([new(PackageStoreMutationKind.Install,
            UploadId: handle.UploadId) { UploadRetention = handle }])));
        var adoption = new RuntimeRegistrySourceAdoptionRequest("example.package", "https://registry.example/", Version: "1.0.0")
            { RuntimePrerequisiteExpectation = new(Hash, "osx-arm64") };
        await Assert.ThrowsAsync<RuntimeProtocolException>(() => client.PreviewPackageRuntimePrerequisitesAsync(new() { Adoption = adoption }));
        await Assert.ThrowsAsync<RuntimeProtocolException>(() => client.AdoptRegistryPackageSourceAsync(adoption));
    }
}
