using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Sunder.Package.Build.Authoring;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderPackageRoleProjectionTests
{
    [Fact]
    public async Task DeclarationOnlyCatalogPreservesSixNaturalSignaturesAndDispatchesExactForeignProvider()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            using Api = Sunder.Generated.Packages.Example_Notes_Services;
            namespace Example;
            [SunderService(SunderServiceExposure.Public), SunderRuntimeUse]
            public interface EmbeddingProviderService
            {
                ValueTask<Entry> DescribeAsync(CancellationToken token = default);
                ValueTask<IReadOnlyList<Entry>> ListModelsAsync(CancellationToken token = default);
                ValueTask<Entry> GetReadinessAsync(CancellationToken token = default);
                ValueTask<Entry> SpaceIdentityAsync(string modelId, CancellationToken token = default);
                ValueTask<Entry> GenerateAsync(string modelId, string text, CancellationToken token = default);
                ValueTask<IReadOnlyList<Entry>> GenerateBatchAsync(string modelId, IReadOnlyList<string> texts, CancellationToken token = default);
            }
            public sealed record Entry(string Value);
            [SunderService]
            public sealed partial class ProbeService(IServiceProviders<Api.IEmbeddingProviderService> providers)
            {
                public async ValueTask<Entry> ReadAsync(CancellationToken token = default)
                    => await (await providers.ListAsync(token)).Providers.Single().Service.GenerateAsync("model", "text", token);
            }
            """;
        var analysis = SunderPackageCompositionPlannerTests.Analyze(source);
        Assert.True(analysis.Success, Errors(analysis));
        var contract = analysis.Compilation.Contracts.Single(value => value.ContractKey == "embedding-provider");
        Assert.Empty(contract.Providers);
        var denied = SunderPackageCompositionPlannerTests.Analyze(source.Replace(", SunderRuntimeUse", "", StringComparison.Ordinal));
        Assert.Contains(denied.RolePlan.Diagnostics, value => value.Id == "package-authoring.di-use-required");

        var load = new AssemblyLoadContext("declaration-catalog", isCollectible: true);
        try
        {
            var worker = EmitCompositionRole(load, analysis, SunderPackageRoleMembership.Worker);
            var api = worker.GetType("Sunder.Generated.Packages.Example_Notes_Services.IEmbeddingProviderService", true)!;
            Assert.Equal(6, api.GetMethods().Length);
            Assert.Equal(new[] { typeof(string), typeof(string), typeof(CancellationToken) }, api.GetMethod("GenerateAsync")!.GetParameters().Select(parameter => parameter.ParameterType));
            Assert.Equal(typeof(IReadOnlyList<string>), api.GetMethod("GenerateBatchAsync")!.GetParameters()[1].ParameterType);
            foreach (var name in new[] { "DescribeAsync", "ListModelsAsync", "GetReadinessAsync" })
                Assert.Equal(typeof(CancellationToken), Assert.Single(api.GetMethod(name)!.GetParameters()).ParameterType);

            var descriptor = contract.Descriptor;
            var rpc = new RoleRpc(descriptor, new SunderWorkerProviderRegistration("example.extension.embedding", descriptor.ContractId,
                descriptor.Version, descriptor.Sha256, descriptor.AbiSha256, new RejectingCompositionHandler()))
            { AllowCatalogDiscovery = true, UnaryResponse = JsonSerializer.SerializeToElement(new { value = "encoded" }) };
            rpc.Provider = rpc.Provider with { PackageId = "example.extension", PackageVersion = "9.0.0" };
            var options = Configure(worker, rpc);
            try
            {
                Assert.Equal(0, rpc.Created);
                var response = await Assert.Single(options.Providers).Handler.InvokeUnaryAsync(null!, "probe", "read", JsonSerializer.SerializeToElement(new { }));
                Assert.Equal("encoded", response.GetProperty("value").GetString());
                Assert.Equal("model", rpc.LastRequest!.Value.GetProperty("modelId").GetString());
                Assert.Equal("text", rpc.LastRequest.Value.GetProperty("text").GetString());
                Assert.Equal(1, rpc.UnaryCalls); Assert.Equal(rpc.Created, rpc.Disposed);
            }
            finally { await options.OnShutdown!(ShutdownReason(), CancellationToken.None); }
        }
        finally { load.Unload(); }
    }
}
