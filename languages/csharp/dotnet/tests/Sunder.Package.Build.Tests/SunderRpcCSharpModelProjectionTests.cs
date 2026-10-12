using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Editor;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpModelProjectionTests
{
    [Fact]
    public void SharedSourceSymbolHasOneAuthorityModelAcrossContractsAndOneGeneratedDeclaration()
    {
        const string source = """
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)] public interface ChatService { ValueTask<Message> SendAsync(Message request); }
            [SunderService(SunderServiceExposure.Public)] public interface RunControlService { ValueTask<Message> ReadAsync(); }
            public sealed record Message(string Text);
            """;
        var result = Compile(source);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.All(result.Contracts, contract => Assert.Equal("Message", contract.ModelProjection.Models["Message"]));
        Assert.True(SymbolEqualityComparer.Default.Equals(result.Contracts[0].SourceTypes.Single().SourceType, result.Contracts[1].SourceTypes.Single().SourceType));
        var inputs = result.Contracts.Select(contract => new SunderRpcCSharpProjectionInput("example.shared", contract.GeneratedLogicalName, contract.Descriptor, contract.ModelProjection)).ToArray();
        var generated = SunderRpcCSharpProjectedBindings.Generate(inputs);
        Assert.Single(generated.Sources, file => file.Path == "models/example.shared/Message.g.cs");
        Assert.Equal(generated.Sources, SunderRpcCSharpProjectedBindings.Generate(inputs.Reverse().ToArray()).Sources);
        Assert.All(generated.Sources.Where(file => file.Path.StartsWith("bindings/", StringComparison.Ordinal)), file => Assert.DoesNotContain("public sealed class Message", file.Text, StringComparison.Ordinal));
        var generatedCompilation = AssertCompiles(generated.Sources);
        var message = generatedCompilation.GetTypeByMetadataName("Sunder.Generated.Packages.Example_Shared_Models.Message");
        Assert.NotNull(message);
        var methods = generatedCompilation.GetSymbolsWithName(name => name is "SendAsync" or "ReadAsync", SymbolFilter.Member).OfType<IMethodSymbol>().ToArray();
        Assert.NotEmpty(methods);
        Assert.All(methods, method => Assert.True(SymbolEqualityComparer.Default.Equals(message, ((INamedTypeSymbol)method.ReturnType).TypeArguments.Single())));
        var export = ProjectExport(result);
        var projectImports = SunderRpcCSharpImportPrepass.PrepareOwnedPackageApi(export, "1.0.0");
        Assert.Single(projectImports, file => file.Path == "models/example.shared/Message.g.cs");
        Assert.All(projectImports, file => Assert.DoesNotContain("Sunder.Sdk.Worker", file.Text, StringComparison.Ordinal));
        Assert.Equal(projectImports, SunderRpcCSharpImportPrepass.PrepareOwnedPackageApi(export with { Contracts = export.Contracts.Reverse().ToArray() }, "1.0.0"));
        AssertCompiles(projectImports.Append(new SunderRpcCSharpSourceFile("consumer.cs", """
            using System.Threading.Tasks;
            using Sunder.Generated.Packages.Example_Shared_Services;
            using Sunder.Generated.Packages.Example_Shared_Models;
            public static class Consumer
            {
                public static async ValueTask<Message> Forward(IChatService chat, IRunControlService control)
                    => await chat.SendAsync(await control.ReadAsync());
            }
            """)));
    }

    [Fact]
    public void EqualSchemasFromDistinctSourceSymbolsDoNotMergeAndReportBothLocations()
    {
        var result = Compile("""
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            namespace One {
                [SunderService(SunderServiceExposure.Public, Key = "first")] public interface FirstService { ValueTask<Message> ReadAsync(); }
                public sealed record Message(string Text);
            }
            namespace Two {
                [SunderService(SunderServiceExposure.Public, Key = "second")] public interface SecondService { ValueTask<Message> ReadAsync(); }
                public sealed record Message(string Text);
            }
            """);
        Assert.False(result.Success);
        var collision = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.generated-name-collision");
        Assert.Contains("One.Message", collision.Message, StringComparison.Ordinal);
        Assert.Contains("Two.Message", collision.Message, StringComparison.Ordinal);
        Assert.NotEmpty(collision.RelatedSpans!);
    }

    [Fact]
    public void VerifiedModelIdentityCanBeReusedByTwoDefinitionKeysInOneContract()
    {
        var result = Compile("""
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)] public interface EchoService { ValueTask<Message> EchoAsync(Message message); }
            public sealed record Message(string Text);
            """);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var contract = Assert.Single(result.Contracts);
        var json = JsonNode.Parse(contract.Descriptor.GetCanonicalUtf8())!;
        json["$defs"]!["AlternateMessage"] = json["$defs"]!["Message"]!.DeepClone();
        json["services"]!["echo"]!["methods"]!["echo"]!["requestSchema"]!["$ref"] = "#/$defs/AlternateMessage";
        var descriptor = SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(json));
        var projection = new SunderRpcModelProjection(new Dictionary<string, string> { ["AlternateMessage"] = "Message", ["Message"] = "Message" },
            new Dictionary<string, SunderRpcServiceReferenceProjection>());
        var generated = SunderRpcCSharpProjectedBindings.Generate([new("example.shared", contract.GeneratedLogicalName, descriptor, projection)]);
        Assert.Single(generated.Sources, file => file.Path == "models/example.shared/Message.g.cs");
        var generatedCompilation = AssertCompiles(generated.Sources);
        var method = Assert.Single(generatedCompilation.GetTypeByMetadataName(contract.GeneratedLogicalName + ".EchoClient")!.GetMembers("EchoAsync").OfType<IMethodSymbol>());
        Assert.True(SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, ((INamedTypeSymbol)method.ReturnType).TypeArguments.Single()));
    }

    [Fact]
    public void ProjectImportRequiresExplicitProjectionEvidence()
    {
        var result = Compile("""
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)] public interface ReadyService { ValueTask<bool> ReadAsync(); }
            """);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var export = ProjectExport(result);
        AssertCompiles(SunderRpcCSharpImportPrepass.PrepareOwnedPackageApi(export, "1.0.0"));
        var missing = export with { Contracts = [export.Contracts[0] with { ModelProjection = null! }] };
        Assert.StartsWith("rpc-authoring.model-projection-invalid:",
            Assert.Throws<InvalidDataException>(() => SunderRpcCSharpImportPrepass.PrepareOwnedPackageApi(missing, "1.0.0")).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedVariantDeclarationIsStableAcrossDirectAndUnionContractsWithoutExposingPrivateModels(bool privateUnion)
    {
        var result = Compile($$"""
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)] public interface DirectService { ValueTask<TextItem> ReadAsync(); }
            [SunderService(SunderServiceExposure.{{(privateUnion ? "Package" : "Public")}})] public interface ChoicesService { ValueTask<Choice> ReadAsync(); }
            [SunderUnion(typeof(TextItem), typeof(EmptyItem)), SunderDiscriminator("kind")]
            public abstract record Choice;
            [SunderVariant("text")] public sealed record TextItem : Choice { public required string Text { get; init; } }
            [SunderVariant("empty")] public sealed record EmptyItem : Choice;
            """);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var inputs = result.Contracts.Select(contract => new SunderRpcCSharpProjectionInput("example.shared", contract.GeneratedLogicalName,
            contract.Descriptor, contract.ModelProjection, contract.Exposure == SunderServiceExposure.Public)).ToArray();
        var generated = SunderRpcCSharpProjectedBindings.Generate(inputs);
        var item = Assert.Single(generated.Sources, source => source.Path == "models/example.shared/TextItem.g.cs");
        Assert.DoesNotContain(generated.Sources, source => source.Path.Contains("CExample_Shared_Direct_Choice", StringComparison.Ordinal));
        Assert.Equal(generated.Sources, SunderRpcCSharpProjectedBindings.Generate(inputs.Reverse().ToArray()).Sources);
        var choice = Assert.Single(generated.Sources, source => source.Path == "models/example.shared/Choice.g.cs");
        if (privateUnion)
        {
            Assert.Contains("internal abstract record Choice", choice.Text, StringComparison.Ordinal);
            Assert.Contains("public sealed record TextItem", item.Text, StringComparison.Ordinal);
        }
        else Assert.Contains("public sealed record TextItem : Choice", item.Text, StringComparison.Ordinal);
        AssertCompiles(generated.Sources);
        AssertCompiles(SunderRpcCSharpImportPrepass.PrepareOwnedPackageApi(ProjectExport(result), "1.0.0"));
    }

    [Fact]
    public void OwnedResourceValueRootsAndNestedNullableContainersCompile()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)]
            public sealed partial class ContentService
            {
                public ValueTask<ContentBox> EchoAsync(ContentBox content) => ValueTask.FromResult(content);
                public ValueTask<Contents> BatchAsync(Contents contents) => ValueTask.FromResult(contents);
            }
            [SunderTransparentValue]
            public readonly record struct ContentBox { public required SunderContent Value { get; init; } }
            public readonly record struct Contents
            {
                public required IReadOnlyList<ContentBox?> Items { get; init; }
                public required IReadOnlyDictionary<string, ContentBox?> ByKey { get; init; }
                public SunderRpcOptional<ContentBox?> Optional { get; init; }
            }
            """;
        var prepass = SunderRpcCSharpOwnedClientPrepass.Compile(new(), new("example.shared", "1.0.0", [new("source.cs", source)], References(), GenerateServiceInterfaces: true));
        Assert.True(prepass.Compilation.Success, string.Join(Environment.NewLine, prepass.Compilation.Diagnostics));
        var contract = Assert.Single(prepass.Compilation.Contracts);
        var plan = SunderRpcCSharpMarshallingPlan.Owned(contract);
        Assert.All(contract.Methods, method =>
        {
            Assert.True(plan.Layouts[method.RequestDefinition].ValueType);
            Assert.True(plan.Layouts[method.OutputDefinition].ValueType);
        });
        AssertCompiles(prepass.Sources.Append(new SunderRpcCSharpSourceFile("source.cs", source))
            .Append(new("authoring.g.cs", SunderRpcCSharpImplementationEmitter.Emit(prepass.Compilation.Contracts)))
            .Concat(SunderRpcCSharpServiceApiEmitter.EmitAppClients(prepass.Compilation.Contracts, "example.shared", "1.0.0", providerSchemasPresent: true)));
    }

    [Fact]
    public void SourceReferenceAndContentProjectWithoutNewLogicalTagsOrAuthoredWrapperRecords()
    {
        const string source = ReferenceContentSource;
        var prepass = SunderRpcCSharpOwnedClientPrepass.Compile(new(), new("example.shared", "1.0.0", [new("source.cs", source)], References(), GenerateServiceInterfaces: true));
        Assert.True(prepass.Compilation.Success, string.Join(Environment.NewLine, prepass.Compilation.Diagnostics));
        var chat = prepass.Compilation.Contracts.Single(contract => contract.ContractKey == "chat");
        var reference = Assert.Single(chat.ModelProjection.ServiceReferences);
        Assert.Equal("example.shared.run.control", reference.Value.ContractId);
        Assert.Equal("run-control", reference.Value.ServiceId);
        var schema = chat.Descriptor.Definitions[reference.Key];
        Assert.False(schema.TryGetProperty("logicalType", out _));
        Assert.Equal(11, schema.GetProperty("properties").GetPropertyCount());
        Assert.Equal(11, schema.GetProperty("required").GetArrayLength());
        Assert.Equal("content-reference", chat.Descriptor.Definitions["Payload"].GetProperty("properties").GetProperty("content").GetProperty("logicalType").GetString());
        Assert.Equal("Payload", chat.ModelProjection.Models["Payload"]);
        Assert.DoesNotContain(prepass.Sources, file => file.Text.Contains("global using", StringComparison.Ordinal));
        var complete = prepass.Sources.Append(new SunderRpcCSharpSourceFile("source.cs", source))
            .Append(new("authoring.g.cs", SunderRpcCSharpImplementationEmitter.Emit(prepass.Compilation.Contracts)))
            .Concat(SunderRpcCSharpServiceApiEmitter.EmitAppClients(prepass.Compilation.Contracts, "example.shared", "1.0.0", providerSchemasPresent: true));
        AssertCompiles(complete);
        AssertCompiles(SunderRpcCSharpImportPrepass.PrepareOwnedPackageApi(ProjectExport(prepass.Compilation), "1.0.0"));
    }

    private static SunderRpcPackageProjectExport ProjectExport(SunderRpcCSharpCompilationResult compilation)
        => new(new string('a', 64), new string('b', 64), "example.shared", "1.0.0",
            compilation.Contracts.Select(contract => new SunderRpcPackageProjectExportContract(contract.ContractKey, contract.Exposure,
                contract.GeneratedLogicalName, contract.Descriptor, contract.ModelProjection) { AppUse = contract.AppUse, RuntimeUse = contract.RuntimeUse, MethodPolicies = contract.MethodPolicies }).ToArray(), new string('c', 64));

    private static CSharpCompilation AssertCompiles(IEnumerable<SunderRpcCSharpSourceFile> sources)
    {
        var compilation = CSharpCompilation.Create("ProjectedModelProbe", sources.Select(source => CSharpSyntaxTree.ParseText(source.Text,
            new CSharpParseOptions(LanguageVersion.CSharp14), source.Path)), References().Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var binary = new MemoryStream(); var result = compilation.Emit(binary);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return compilation;
    }

    private static SunderRpcCSharpCompilationResult Compile(string source)
        => new SunderRpcCSharpSourceCompiler().Compile(new("example.shared", "1.0.0", [new("source.cs", source)], References()));
    private static string[] References() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Append(typeof(SunderServiceAttribute).Assembly.Location).Append(typeof(Sunder.Sdk.Worker.SunderWorker).Assembly.Location).Distinct(StringComparer.Ordinal).ToArray();
}
