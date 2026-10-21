using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpModelProjectionTests
{
    [Theory]
    [InlineData(JsonIgnoreCondition.WhenWritingNull)]
    [InlineData(JsonIgnoreCondition.WhenWritingDefault)]
    public void ProjectedNullableRequiredPropertiesDefaultToNullButRemainRequiredOnTheWire(JsonIgnoreCondition ignore)
    {
        var result = Compile("""
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using System.Text.Json.Serialization;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)] public interface ResultsService { ValueTask<Result> EchoAsync(Result value); }
            public sealed record Child(string Text);
            public sealed record Result
            {
                [JsonPropertyName("wire-note")]
                public required string? Note { get; init; }
                public required int? Count { get; init; }
                public required DateTimeOffset? Created { get; init; }
                public required Guid? Id { get; init; }
                public required Child? Child { get; init; }
                public required IReadOnlyList<Child?>? Items { get; init; }
                public required IReadOnlyDictionary<string, Child?>? ByKey { get; init; }
                public required byte[]? Data { get; init; }
                public SunderRpcOptional<string?> OptionalText { get; init; }
                public SunderRpcOptional<long> OptionalNumber { get; init; }
            }
            """);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var contract = Assert.Single(result.Contracts);
        var descriptorBytes = contract.Descriptor.GetCanonicalUtf8();
        var descriptorHash = contract.Descriptor.Sha256;
        var abiHash = contract.Descriptor.AbiSha256;
        var generated = SunderRpcCSharpProjectedBindings.Generate([new("example.shared", contract.GeneratedLogicalName,
            contract.Descriptor, contract.ModelProjection)]);
        var (load, probe) = LoadProjectionProbe(generated.Sources.Append(new("nullable-required.cs", """
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using Sunder.Sdk.Rpc;
            using Sunder.Generated.Packages.Example_Shared_Models;
            public static class NullableRequiredProbe
            {
                public static object Create() => new Result { };
                public static object Clone(object value) => ((Result)value) with
                    { WireNote = "changed", OptionalText = new SunderRpcOptional<string?>(null), OptionalNumber = 0 };
                public static JsonElement Encode(object value, JsonIgnoreCondition ignore)
                    => JsonSerializer.SerializeToElement((Result)value, Options(ignore));
                public static object Decode(JsonElement value) => value.Deserialize<Result>(Options(JsonIgnoreCondition.Never))!;
                public static bool HasOptionalText(object value) => ((Result)value).OptionalText.IsPresent;
                private static JsonSerializerOptions Options(JsonIgnoreCondition ignore)
                    => new() { DefaultIgnoreCondition = ignore, Converters = { new SunderRpcJsonConverterFactory() } };
            }
            """)), "NullableRequiredProbe");
        try
        {
            var create = probe.GetMethod("Create")!.CreateDelegate<Func<object>>();
            var clone = probe.GetMethod("Clone")!.CreateDelegate<Func<object, object>>();
            var encode = probe.GetMethod("Encode")!.CreateDelegate<Func<object, JsonIgnoreCondition, JsonElement>>();
            var decode = probe.GetMethod("Decode")!.CreateDelegate<Func<JsonElement, object>>();
            var present = probe.GetMethod("HasOptionalText")!.CreateDelegate<Func<object, bool>>();
            var value = create();
            var wire = encode(value, ignore);
            var required = contract.Descriptor.Definitions["Result"].GetProperty("required").EnumerateArray().Select(item => item.GetString()!).Order().ToArray();
            Assert.Equal(new[] { "byKey", "child", "count", "created", "data", "id", "items", "wire-note" }, required);
            Assert.Equal(required, wire.EnumerateObject().Select(property => property.Name).Order());
            Assert.All(wire.EnumerateObject(), property => Assert.Equal(JsonValueKind.Null, property.Value.ValueKind));
            Assert.True(contract.Descriptor.IsValid("#/$defs/Result", wire, out _));
            Assert.False(present(decode(wire)));
            foreach (var member in required)
            {
                var missing = JsonNode.Parse(wire.GetRawText())!.AsObject();
                Assert.True(missing.Remove(member));
                var input = JsonSerializer.SerializeToElement(missing);
                Assert.False(contract.Descriptor.IsValid("#/$defs/Result", input, out _));
                Assert.Throws<JsonException>(() => decode(input));
            }
            var copy = clone(value);
            Assert.NotSame(value, copy);
            var changed = encode(copy, ignore);
            Assert.Equal("changed", changed.GetProperty("wire-note").GetString());
            Assert.Equal(JsonValueKind.Null, changed.GetProperty("optionalText").ValueKind);
            Assert.Equal(0, changed.GetProperty("optionalNumber").GetInt64());
            Assert.True(present(decode(changed)));
            Assert.Equal(wire.GetRawText(), encode(value, ignore).GetRawText());
        }
        finally { load.Unload(); }
        Assert.Equal(descriptorBytes, contract.Descriptor.GetCanonicalUtf8());
        Assert.Equal(descriptorHash, contract.Descriptor.Sha256);
        Assert.Equal(abiHash, contract.Descriptor.AbiSha256);
    }

    [Fact]
    public void ProjectedNullableRequiredPropertiesKeepJsonAliasesTransparentValuesAndNonnullableMembersRequired()
    {
        var result = Compile("""
            using System;
            using System.Text.Json;
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)] public interface GuardsService { ValueTask<Guarded> ReadAsync(); }
            [SunderTransparentValue] public readonly record struct NullableText { public required string? Value { get; init; } }
            public sealed record Guarded
            {
                public required string Text { get; init; }
                public required long Count { get; init; }
                public required DateTimeOffset Created { get; init; }
                public required JsonElement Raw { get; init; }
                public required JsonElement AliasedRaw { get; init; }
                public required NullableText Wrapped { get; init; }
                public required string? Note { get; init; }
            }
            """);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var contract = Assert.Single(result.Contracts);
        // Unmapped descriptor aliases collapse to their CLR representation; mapped
        // transparent values keep a named struct. Neither may confuse JSON null with a CLR null default.
        var json = JsonNode.Parse(contract.Descriptor.GetCanonicalUtf8())!;
        json["$defs"]!["RawJson"] = new JsonObject();
        json["$defs"]!["JsonAlias"] = new JsonObject { ["$ref"] = "#/$defs/RawJson" };
        json["$defs"]!["NullableAlias"] = json["$defs"]!["Guarded"]!["properties"]!["note"]!.DeepClone();
        json["$defs"]!["Guarded"]!["properties"]!["aliasedRaw"] = new JsonObject { ["$ref"] = "#/$defs/JsonAlias" };
        json["$defs"]!["Guarded"]!["properties"]!["note"] = new JsonObject { ["$ref"] = "#/$defs/NullableAlias" };
        var descriptor = SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(json));
        var generated = SunderRpcCSharpProjectedBindings.Generate([new("example.shared", contract.GeneratedLogicalName, descriptor, contract.ModelProjection)]);
        var compilation = AssertCompiles(generated.Sources);
        var type = compilation.GetTypeByMetadataName("Sunder.Generated.Packages.Example_Shared_Models.Guarded")!;
        string[] required = ["AliasedRaw", "Count", "Created", "Raw", "Text", "Wrapped"];
        Assert.Equal(required, type.GetMembers().OfType<IPropertySymbol>().Where(property => property.IsRequired).Select(property => property.Name).Order());
        Assert.True(type.IsRecord);
        var invalid = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("""
            public static class MissingRequiredProbe
            { public static object Create() => new Sunder.Generated.Packages.Example_Shared_Models.Guarded { }; }
            """, new CSharpParseOptions(LanguageVersion.CSharp14)));
        var errors = invalid.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.Equal(required.Length, errors.Length);
        Assert.All(errors, diagnostic => Assert.Equal("CS9035", diagnostic.Id));
        Assert.All(required, name => Assert.Contains(errors, diagnostic => diagnostic.GetMessage().Contains("Guarded." + name, StringComparison.Ordinal)));
        AssertCompiles(generated.Sources.Append(new("construct.cs", """
            using System.Text.Json;
            using Sunder.Generated.Packages.Example_Shared_Models;
            public static class MixedRequiredProbe
            {
                public static Guarded Copy(Guarded source) => new()
                {
                    Text = source.Text, Count = source.Count, Created = source.Created,
                    Raw = JsonSerializer.SerializeToElement<object?>(null),
                    AliasedRaw = JsonSerializer.SerializeToElement<object?>(null), Wrapped = new NullableText(null)
                };
            }
            """)));

        // The public advanced generator keeps its historical required/class surface.
        var advanced = SunderRpcCSharpGenerator.Generate(descriptor, "Advanced");
        var advancedType = AssertCompiles([new("advanced.cs", advanced)]).GetTypeByMetadataName("Advanced.Guarded")!;
        Assert.False(advancedType.IsRecord);
        Assert.Equal(required.Append("Note").Order(), advancedType.GetMembers().OfType<IPropertySymbol>()
            .Where(property => property.IsRequired).Select(property => property.Name).Order());
        Assert.DoesNotContain("JsonRequired", advanced, StringComparison.Ordinal);
        Assert.Contains("public required string? Note { get; init; }", advanced, StringComparison.Ordinal);
        Assert.Contains("public required string? Wrapped { get; init; }", advanced, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectedNullableRequiredResourcesRetainTypedMembersAndWithClones()
    {
        var prepass = SunderRpcCSharpOwnedClientPrepass.Compile(new(), new("example.shared", "1.0.0", [new("source.cs", """
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            using Api = Sunder.Generated.Packages.Example_Shared_Services;
            [SunderService(SunderServiceExposure.Public)] public interface RunControlService { ValueTask<bool> ReadyAsync(); }
            [SunderService(SunderServiceExposure.Public)] public interface ResourceService { ValueTask<ResourceResult> ReadAsync(); }
            public sealed record ResourceResult
            {
                public required SunderContent? Content { get; init; }
                public required ServiceReference<Api.IRunControlService>? Control { get; init; }
            }
            """)], References(), GenerateServiceInterfaces: true));
        Assert.True(prepass.Compilation.Success, string.Join(Environment.NewLine, prepass.Compilation.Diagnostics));
        var sources = SunderRpcCSharpImportPrepass.PrepareOwnedPackageApi(ProjectExport(prepass.Compilation), "1.0.0");
        var (load, probe) = LoadProjectionProbe(sources.Append(new("resources.cs", """
            using Sunder.Sdk.Services;
            using Sunder.Generated.Packages.Example_Shared_Models;
            using Sunder.Generated.Packages.Example_Shared_Services.CExample_Shared_Run_Control;
            public static class NullableResourceProbe
            {
                public static object Create() => new ResourceResult { };
                public static object Clone(object value, SunderContent content) => ((ResourceResult)value) with { Content = content };
                public static SunderContent? Content(object value) => ((ResourceResult)value).Content;
                public static ServiceReference<IRunControlService>? Control(object value) => ((ResourceResult)value).Control;
            }
            """)), "NullableResourceProbe");
        try
        {
            var create = probe.GetMethod("Create")!.CreateDelegate<Func<object>>();
            var clone = probe.GetMethod("Clone")!.CreateDelegate<Func<object, SunderContent, object>>();
            var content = probe.GetMethod("Content")!.CreateDelegate<Func<object, SunderContent?>>();
            var original = create();
            Assert.Null(content(original));
            Assert.Null(probe.GetMethod("Control")!.Invoke(null, [original]));
            await using var value = SunderContent.FromBytes(new byte[] { 1, 2, 3 });
            var copy = clone(original, value);
            Assert.NotSame(original, copy);
            Assert.Same(value, content(copy));
            Assert.Null(content(original));
        }
        finally { load.Unload(); }
    }
}
