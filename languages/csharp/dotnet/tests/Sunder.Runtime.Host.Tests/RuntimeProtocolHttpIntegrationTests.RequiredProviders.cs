using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RuntimeProtocolHttpIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredProviderProblemsPreserveTypedDetailsThroughTheHttpClient(bool cycle)
    {
        var predicate = RuntimeRequiredProviderPlanTests.Predicate("example.consumer");
        var code = cycle ? RpcReadinessErrorCodes.RequiredProviderCycle : RpcReadinessErrorCodes.RequiredProviderUnsatisfied;
        await using var server = await RuntimeHttpServer.StartAsync(app => app.MapGet("/api/v1/graph-failure", (Func<IResult>)(() =>
            throw new RuntimeRequiredProviderException(code, [predicate.ConsumerPackageId], [predicate]))));
        using var transport = new RuntimeClientTransport(() => new RuntimeConnectionInfo(server.BaseUri, RuntimeHttpServer.Token));
        using var response = await transport.GetAsync(transport.CreateRequestUri("graph-failure"));
        var failure = await Assert.ThrowsAsync<RuntimeClientException>(() => new RuntimeHttpResponseReader(new()).EnsureSuccessAsync(response, default));
        Assert.Equal(HttpStatusCode.Conflict, failure.StatusCode);
        Assert.Equal(code, failure.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(failure.CorrelationId));
        var details = failure.Details!.Value;
        Assert.Equal("example.consumer", details.GetProperty("predicates")[0].GetProperty("consumerPackageId").GetString());
        Assert.Equal(cycle, details.TryGetProperty("packageIds", out _));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("store")]
    public async Task RequiredProviderAdmissionReturnsConflictAndLeavesThePriorGenerationUnchanged(string operation)
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-required-provider-http");
        using var policy = new ActivePolicyTestContext();
        RuntimeLocalState.EnsureInitialized(policy.Paths.RootPath);
        var source = temporary.GetPath("source");
        var descriptor = SunderRpcContractDescriptor.Parse("""
            {"descriptorVersion":1,"schemaProfile":"sunder.rpc.values.v1","contractId":"example.required","version":"1.0.0","errors":{},
             "services":{"service":{"methods":{"call":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Request"}}}}},
             "$defs":{"Request":{"type":"object","properties":{},"required":[],"additionalProperties":false}}}
            """u8.ToArray());
        CanonicalPackageTestBuilder.WriteExplodedPackage(source, "example.consumer", "1.0.0", typeof(RuntimeProtocolHttpIntegrationTests).Assembly.Location,
            roles: ["app"], contractBundles: [new()
            {
                Source = "owned", AuthorityKind = "package", AuthorityId = "example.consumer", ContractId = descriptor.ContractId, Version = descriptor.Version,
                DescriptorPath = "contracts/required.json", DescriptorSha256 = descriptor.Sha256, AbiSha256 = descriptor.AbiSha256, Exposure = "public",
            }], usesContracts: [new() { Role = "app", ContractId = descriptor.ContractId, ContractVersion = descriptor.Version, AbiSha256 = descriptor.AbiSha256, Required = true, Actions = ["discover", "invoke"] }]);
        Directory.CreateDirectory(Path.Combine(source, "payload", "shared", "contracts"));
        await File.WriteAllBytesAsync(Path.Combine(source, "payload", "shared", "contracts", "required.json"), descriptor.GetCanonicalUtf8());
        CanonicalPackageTestBuilder.WriteContentIndex(source);
        await policy.RefreshAsync(packages: [new("example.consumer", "1.0.0", PackageSessionPreparer.ComputeContentIdentity(source))]);
        await using var server = await RuntimeHttpServer.StartAsync(app => app.MapGroup("/api/v1").MapPackageSessionEndpoints().MapInstalledPackageEndpoints().MapContentTransferEndpoints(), services =>
        {
            services.AddRuntimeHostServices(policy.Paths, new RuntimeBearerTokenValidator(RuntimeHttpServer.Token));
            services.AddSingleton(policy.Cache);
            services.AddSingleton<IPolicyContinuousClock>(policy.Clock);
        });
        var owner = server.Services.GetRequiredService<RuntimeSessionOwner>();
        var generation = owner.Generation;
        using var transport = new RuntimeClientTransport(() => new RuntimeConnectionInfo(server.BaseUri, RuntimeHttpServer.Token));
        using var management = new RuntimeManagementClient(transport);
        RuntimeClientException failure;
        if (operation == "store")
        {
            var archive = temporary.GetPath("consumer.sunderpkg");
            ZipFile.CreateFromDirectory(source, archive);
            var upload = await management.UploadPackageAsync(archive);
            failure = await Assert.ThrowsAsync<RuntimeClientException>(() => management.StagePackageStoreChangesAsync(
                new([new(PackageStoreMutationKind.Install, UploadId: upload.UploadId)])));
        }
        else
        {
            var sources = owner.Sources.Snapshot();
            sources.SetDevOverlay(new("example.consumer", source, false, PackageSessionOverlayOwner.Startup));
            owner.Sources.Replace(sources);
            using var response = await transport.SendAsync(new(HttpMethod.Post, transport.CreateRequestUri("packages/session/stage"))
                { Content = JsonContent.Create(new PackageLifecycleStageRequest(["example.consumer"])) });
            failure = await Assert.ThrowsAsync<RuntimeClientException>(() => new RuntimeHttpResponseReader(new()).EnsureSuccessAsync(response, default));
        }
        Assert.Equal(HttpStatusCode.Conflict, failure.StatusCode);
        Assert.Equal(RpcReadinessErrorCodes.RequiredProviderUnsatisfied, failure.ErrorCode);
        Assert.Equal(descriptor.Sha256, failure.Details!.Value.GetProperty("predicates")[0].GetProperty("contract").GetProperty("descriptorSha256").GetString());
        Assert.Equal(generation, owner.Generation);
        Assert.Empty(owner.State.GetActivePackages());
        Assert.Empty(await server.Services.GetRequiredService<InstalledPackageStore>().ListAsync());
        Assert.False(server.Services.GetRequiredService<PackageStoreCoordinator>().HasPreparedStages);
    }
}
