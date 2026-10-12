using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpSourceCompilerTests
{
    [Theory]
    [InlineData("public sealed class Value { public string Id { get; init; } = \"local\"; }", "must be declared required")]
    [InlineData("public sealed class Value { public Value(string id) { Id = id; } public Value(int id) { Id = id.ToString(); } public string Id { get; } }", "unique constructor")]
    [InlineData("public sealed record Value(string Id) { public string Unbound { get; set; } = \"local\"; }", "must be declared required")]
    [InlineData("public sealed class Value { public Value(string id) { Id = id; } public string Id { get; } public bool Unbound { get; init; } }", "must be declared required")]
    [InlineData("public sealed class Value { public Value(string missing = \"default\") { } public string Id { get; init; } = \"local\"; }", "must bind exactly one")]
    [InlineData("public sealed class Value { [JsonConstructor] private Value(string id) { Id = id; } public string Id { get; } }", "accessible constructor")]
    [InlineData("public sealed record Value(string Id) { [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Ignored { get; init; } }", "explicit SunderRpcOptional")]
    [InlineData("public sealed record Value(string Id) { [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public int Count { get; init; } }", "representation overrides")]
    [InlineData("public sealed record Value(string Id) { [JsonConverter(typeof(JsonStringEnumConverter))] public string Text { get; init; } = \"\"; }", "representation overrides")]
    [InlineData("public sealed record Value(string Id) { [JsonIgnore, SunderServiceMember, JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public int Count { get; init; } }", "representation overrides")]
    [InlineData("public sealed record Value(string Id, [property: JsonIgnore] string Secret);", "ignored constructor parameter")]
    [InlineData("public sealed record Value([property: JsonIgnore] string Secret);", "ignored constructor parameter")]
    [InlineData("public sealed class Value { public Value(string id) { Id = id; } public string? Id { get; } }", "same-typed source property")]
    [InlineData("public sealed class Value { public Value(System.Collections.Generic.IReadOnlyList<string> items) { Items = items; } public System.Collections.Generic.IReadOnlyList<string>? Items { get; } }", "same-typed source property")]
    [InlineData("public sealed class Value { public Value(System.Collections.Generic.IReadOnlyList<string>? items) { Items = new string?[] { null }; } public System.Collections.Generic.IReadOnlyList<string?> Items { get; } }", "same-typed source property")]
    [InlineData("public sealed class Value { public Value(System.Collections.Generic.IEnumerable<string>? items) { Items = new string[0]; } public System.Collections.Generic.IReadOnlyList<string> Items { get; } }", "same-typed source property")]
    [InlineData("public sealed class Value { public Value(long? count) { Count = 0; } public int Count { get; } }", "same-typed source property")]
    [InlineData("public sealed class Value { public Value(string wire) { Id = wire; } [JsonPropertyName(\"wire\")] public string Id { get; } }", "by CLR name")]
    [InlineData("public sealed class Value { public Value(string id) { Id = id; } public string Id { get; } public int Extra; }", "public fields")]
    public void ConstructorValues_RejectAmbiguousUnboundAndSerializerOverriddenShapes(string declaration, string message)
    {
        var result = CompileSource("""
            using System.Threading.Tasks;
            using System.Text.Json.Serialization;
            using Sunder.Sdk.Services;
            [SunderService] public sealed partial class ValuesService { public Task<Value> EchoAsync(Value value) => Task.FromResult(value); }
            """ + declaration);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.value-invalid" && diagnostic.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void ConstructorValues_DefaultsAndMethodDefaultsDoNotAuthorizeMissingWireValues()
    {
        var result = CompileSource("""
            using System.Threading.Tasks;
            using System.Text.Json.Serialization;
            using Sunder.Sdk.Services;
            [SunderService] public sealed partial class ValuesService
            {
                public Task<Value> EchoAsync(Value? value = null, bool enabled = false) => Task.FromResult(value ?? new Value("local"));
            }
            public sealed class Value
            {
                public Value(int unused) => throw new System.Exception();
                [JsonConstructor] public Value(string id, bool enabled = true) { Id = id; Enabled = enabled; }
                [JsonPropertyName("wire-id")] public string Id { get; }
                public bool Enabled { get; }
            }
            """);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var descriptor = Assert.Single(result.Contracts).Descriptor;
        Assert.Equal(new[] { "enabled", "value" }, descriptor.Definitions["ValuesEchoRequest"].GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "enabled", "wire-id" }, descriptor.Definitions["Value"].GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));
        var generated = SunderRpcCSharpImplementationEmitter.Emit(result.Contracts);
        Assert.Contains("new global::Value(", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorValues_BindingHasASharedBoundForParametersAndProperties()
    {
        var result = CompileSource("using System.Threading.Tasks; using Sunder.Sdk.Services; [SunderService] public sealed partial class ValuesService { public Task<Value> EchoAsync(Value value) => Task.FromResult(value); } public sealed record Value("
            + string.Join(",", Enumerable.Range(0, 257).Select(index => "int P" + index)) + ");");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.value-invalid"
            && diagnostic.Message.Contains("at most 256", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[SunderServiceMember, JsonIgnore] internal System.Action? DispatchStarted { get; init; }")]
    [InlineData("[SunderServiceMember, JsonIgnore] public System.Action? DispatchStarted { get; init; }")]
    [InlineData("[SunderServiceMember, JsonIgnore] private string Secret { get; init; } = \"\";")]
    [InlineData("[SunderServiceMember, JsonIgnore] public static string Secret { get; set; } = \"\";")]
    public void ServiceMember_RejectsLocalCallbacksAndNonPublicOrStaticProperties(string member)
    {
        var result = CompileSource("using System.Threading.Tasks; using System.Text.Json.Serialization; using Sunder.Sdk.Services; [SunderService] public sealed partial class ValuesService { public Task<Value> EchoAsync(Value value) => Task.FromResult(value); } public sealed record Value(string Id) { " + member + " }");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.service-member-invalid" && diagnostic.Path is not null && diagnostic.Line > 0);
    }

    [Theory]
    [InlineData("SunderRpcInvocationContext first, string value, SunderRpcInvocationContext second")]
    [InlineData("System.Threading.CancellationToken token, string value")]
    [InlineData("System.Threading.CancellationToken token, SunderRpcInvocationContext context, string value")]
    public void OrdinaryContext_RejectsDuplicateContextAndWireArgumentsAfterCancellation(string parameters)
    {
        var result = CompileSource("using System.Threading.Tasks; using Sunder.Sdk.Rpc; using Sunder.Sdk.Services; [SunderService] public sealed partial class ValuesService { public Task<string> EchoAsync("
            + parameters + ") => Task.FromResult(value); }");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.method-invalid");
    }

    [Theory]
    [InlineData("[System.Flags] public enum Mode { One = 1, Two = 2 }")]
    [InlineData("public enum Mode { One = 1, Alias = 1 }")]
    [InlineData("public enum Mode : ulong { TooLarge = 9007199254740992UL }")]
    [InlineData("public enum Mode { [System.Text.Json.Serialization.JsonStringEnumMemberName(\"one\")] One, Two }")]
    public void OrdinaryEnumValues_StayClosedAndRejectFlagsAliasesUnsafeIntegersOrPartialStringNames(string declaration)
    {
        var result = CompileSource("using System.Threading.Tasks; using Sunder.Sdk.Services; [SunderService] public sealed partial class ValuesService { public Task<Mode> EchoAsync(Mode mode) => Task.FromResult(mode); } " + declaration);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.value-invalid");
    }
}
