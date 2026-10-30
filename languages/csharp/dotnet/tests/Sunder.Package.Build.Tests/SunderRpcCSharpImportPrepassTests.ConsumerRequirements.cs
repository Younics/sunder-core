using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpImportPrepassTests
{
    [Fact]
    public void ConsumerRequirements_IncludeReferencedServiceMethodsWithoutRequiringUnrelatedInventory()
    {
        var imports = PrepareMigrationImports("""
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            using Api = Sunder.Generated.Packages.Example_Dependency_Services;
            public record Selection(ServiceReference<Api.IChildService> Child);
            [SunderService(SunderServiceExposure.Public, ContractId = "example.child", ServiceId = "child")]
            public interface ChildService { ValueTask<string> ReadAsync(); }
            [SunderService(SunderServiceExposure.Public, ContractId = "example.parent", ServiceId = "parent")]
            public interface ParentService { ValueTask<Selection> SelectAsync(); }
            [SunderService(SunderServiceExposure.Public, ContractId = "example.unrelated", ServiceId = "other")]
            public interface OtherService { ValueTask<string> UnrelatedAsync(); }
            """, generateServiceInterfaces: true);
        var result = new SunderRpcCSharpSourceCompiler().Compile(new("example.consumer", "1.0.0", [new("consumer.cs", """
            using Sunder.Generated.Packages.Example_Dependency_Services.CExample_Parent;
            public static class Consumer { public static object Read(IParentService service) => service.SelectAsync(); }
            """)], References(), SupplementalSources: imports.Sources, ImportedPackageApis: imports.PackageApis));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal([new SunderRpcRequiredMethod("child", "read")], result.ConsumerRequirements.Single(value => value.Source.ContractId == "example.child").Methods);
        Assert.Empty(result.ConsumerRequirements.Single(value => value.Source.ContractId == "example.unrelated").Methods);
        var parent = result.ConsumerRequirements.Single(value => value.Source.ContractId == "example.parent");
        Assert.NotEmpty(parent.ExactOutputDefinitions);
        Assert.Equal([new SunderRpcRequiredMethod("parent", "select")], parent.Methods);
    }

    [Theory]
    [InlineData("call")]
    [InlineData("delegate")]
    [InlineData("unrelated")]
    public void ConsumerRequirements_CollectTypedReferencesNotGeneratedBodiesOrMatchingNames(string kind)
    {
        var imports = SunderRpcCSharpImportPrepass.Prepare(_lock, _root);
        var body = kind switch
        {
            "call" => "public static ValueTask Run(IExampleService service) => service.PingAsync();",
            "delegate" => "public static Func<CancellationToken, ValueTask> Capture(IExampleService service) => service.PingAsync;",
            _ => "public static ValueTask PingAsync() => ValueTask.CompletedTask; public static ValueTask Run() => PingAsync();",
        };
        var source = "using System; using System.Threading; using System.Threading.Tasks; using Sunder.Generated.Packages.Example_Dependency_Services.CExample_Rpc_V1_Minimal; public static class Consumer { " + body + " }";
        var compiled = new SunderRpcCSharpSourceCompiler().Compile(new("example.consumer", "1.0.0", [new("consumer.cs", source)], References(),
            SupplementalSources: imports.Sources, ImportedPackageApis: imports.PackageApis));
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        var requirements = Assert.Single(compiled.ConsumerRequirements);
        Assert.Equal(kind == "unrelated" ? [] : new[] { new SunderRpcRequiredMethod("example", "ping") }, requirements.Methods);
        var receipt = SunderRpcAuthoringDocuments.WriteAuthoringReceipt("1.1.0", new string('a', 64), _lock,
            "example.consumer", "1.0.0", compiled, imports);
        var projection = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(receipt, _lock.Sha256, "example.consumer", "1.0.0");
        Assert.Equal(requirements.ToCanonicalUtf8(), Assert.Single(projection.ConsumerRequirements).ToCanonicalUtf8());
        Assert.Equal(_descriptor.Sha256, Assert.Single(projection.Contracts).Value.Sha256);
        Assert.Single(projection.Manifest.PackageArtifactImports!); // Complete provenance is retained.
        using var document = JsonDocument.Parse(receipt);
        Assert.Equal(1, document.RootElement.GetProperty("packageArtifacts")[0].GetProperty("contracts").GetArrayLength());
        var altered = System.Text.Json.Nodes.JsonNode.Parse(receipt)!;
        altered["consumerRequirements"]![0]!["descriptorSha256"] = new string('f', 64);
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(
            JsonSerializer.SerializeToUtf8Bytes(altered), _lock.Sha256, "example.consumer", "1.0.0"));
    }

    [Theory]
    [InlineData("explicit", true)]
    [InlineData("undeclared", false)]
    [InlineData("unknown-method", false)]
    [InlineData("full-model", false)]
    public void ConsumerRequirements_RawCallsRequireExplicitMethodsAndViewsCannotNarrowTypedModels(string scenario, bool success)
    {
        var imports = PrepareMigrationImports("""
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            public record Profile(string Name, string Unrelated);
            [SunderService(SunderServiceExposure.Public, ContractId = "example.view", ServiceId = "items")]
            public interface ItemService { ValueTask<Profile> ReadAsync(); }
            """);
        var attributes = scenario == "undeclared" ? "" : $$"""
            [assembly: Sunder.Sdk.Services.SunderRpcDynamicUse("example.view", "items", "{{(scenario == "unknown-method" ? "missing" : "read")}}")]
            [assembly: Sunder.Sdk.Services.SunderRpcConsumerView("example.view", "Profile", "name")]
            """;
        var source = $$"""
            using System.Text.Json;
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            {{attributes}}
            public static class Consumer {
                public static ValueTask<JsonElement> Read(ISunderRpcClient rpc, SunderRpcEndpointReference endpoint, JsonElement request)
                    => rpc.InvokeAsync(endpoint, "items", "read", request);
                {{(scenario == "full-model" ? "public static object Full(Sunder.Generated.Packages.Example_Dependency_Services.CExample_View.IItemsService service) => service.ReadAsync();" : "")}}
            }
            """;
        var result = new SunderRpcCSharpSourceCompiler().Compile(new("example.consumer", "1.0.0", [new("consumer.cs", source)], References(),
            SupplementalSources: imports.Sources, ImportedPackageApis: imports.PackageApis));
        Assert.Equal(success, result.Success);
        if (!success) { Assert.Contains(result.Diagnostics, value => value.Id.StartsWith("rpc-authoring.", StringComparison.Ordinal)); return; }
        var requirement = Assert.Single(result.ConsumerRequirements);
        Assert.Equal([new SunderRpcRequiredMethod("items", "read")], requirement.Methods);
        Assert.Equal(["name"], requirement.OutputMembers["Profile"]);
        Assert.DoesNotContain("Profile", requirement.ExactOutputDefinitions);
    }
}
