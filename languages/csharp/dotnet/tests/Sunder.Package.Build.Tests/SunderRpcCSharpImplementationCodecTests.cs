using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class SunderRpcCSharpImplementationCodecTests
{
    [Fact]
    public async Task Unary_PreservesRequiredDefaultsAndOptionalPresence()
    {
        using var probe = CompileProbe();
        foreach (var json in new[]
                 {
                     DefaultsJson,
                     """{"flag":false,"count":0,"text":null,"number":null,"optionalFlag":false,"optionalCount":0,"optionalText":null}""",
                     """{"flag":true,"count":12,"text":"value","number":4,"optionalFlag":true,"optionalCount":3,"optionalText":"present"}""",
                 })
        {
            var response = await probe.RoundTrip("echo", json);
            AssertJson(json, response);
        }
        Assert.Equal(3, probe.Calls());
    }

    [Fact]
    public async Task Stream_PreservesDefaultsAndDistinguishesAbsentFromPresentNullAndZero()
    {
        using var probe = CompileProbe();
        var events = await probe.Stream("watch", DefaultsJson);
        Assert.Equal(2, events.Count);
        AssertJson(DefaultsJson, events[0]);
        AssertJson("""{"flag":false,"count":0,"text":null,"number":null,"optionalFlag":false,"optionalCount":0,"optionalText":null}""", events[1]);
        Assert.False(events[0].TryGetProperty("optionalText", out _));
        Assert.Equal(JsonValueKind.Null, events[1].GetProperty("optionalText").ValueKind);
        Assert.Equal(1, probe.Calls());
    }

    [Fact]
    public async Task TransparentRoots_ConstructAuthoredWrappersAndWriteUnderlyingValues()
    {
        using var probe = CompileProbe();
        (string Method, string Json)[] cases =
        [
            ("flag", "false"),
            ("count", "0"),
            ("text", "\"\""),
            ("list", "[]"),
            ("list", "[\"one\",null,\"two\"]"),
            ("map", "{}"),
            ("map", """{"zero":0,"other":9,"Count":0}"""),
            ("null-text", "null"),
            ("null-text", "\"present\""),
            ("wrapped-null-text", "null"),
            ("wrapped-null-text", "\"present\""),
            ("null-count", "null"),
            ("null-count", "0"),
            ("content", ContentJson),
            ("any", "null"),
            ("any", """{"arbitrary":[0,false,null]}"""),
        ];
        foreach (var (method, json) in cases)
        {
            AssertJson(json, await probe.RoundTrip(method, json));
        }
        Assert.Equal(cases.Length, probe.Calls());
    }

    [Fact]
    public async Task NestedWrappers_UseTheSameCodecsInRecordsListsMapsOptionalsAndStreams()
    {
        using var probe = CompileProbe();
        var json = $$"""
            {
              "label":"nested","count":0,"flag":false,
              "items":["",null,"value"],"scores":{"zero":0},
              "nullableLabel":null,"nullableCount":null,"nullText":null,"nullCount":null,
              "content":{{ContentJson}},"optionalLabel":null,"optionalCount":0,
              "records":[{{DefaultsJson}}],"recordsByKey":{"first":{{DefaultsJson}}}
            }
            """;
        AssertJson(json, await probe.RoundTrip("nested", json));
        Assert.True(probe.NullableOutersWereNull());
        AssertJson(json, Assert.Single(await probe.Stream("wrapped-watch", json)));
        Assert.True(probe.NullableOutersWereNull());

        var populated = JsonNode.Parse(json)!.AsObject();
        populated["nullableLabel"] = "outer-present";
        populated["nullableCount"] = 0;
        populated["nullText"] = "inner-present";
        populated["nullCount"] = 0;
        populated.Remove("optionalLabel");
        populated.Remove("optionalCount");
        AssertJson(populated.ToJsonString(), await probe.RoundTrip("nested", populated.ToJsonString()));
        Assert.False(probe.NullableOutersWereNull());
    }

    [Theory]
    [InlineData("byte", "0", "255")]
    [InlineData("sbyte", "-128", "127")]
    [InlineData("short", "-32768", "32767")]
    [InlineData("ushort", "0", "65535")]
    [InlineData("int", "-2147483648", "2147483647")]
    public async Task PrimitiveIntegers_AcceptRawIntegerSpellingsInWrappersRecordsListsMapsOptionalsAndStreams(
        string clrType, string minimum, string maximum)
    {
        using var probe = CompileProbe(clrType);
        (string Json, string Expected)[] cases =
        [
            ("1.0", "1"), ("1e0", "1"), ("10e-1", "1"), ("1.000", "1"), ("0.1e1", "1"), ("1E+2", "100"),
            ("-0", "0"), ("-0.0", "0"), ("-0e99", "0"), (minimum + ".0", minimum), (maximum + "e0", maximum),
        ];
        foreach (var (json, expected) in cases)
        {
            var scalar = await probe.RoundTrip("integer", json);
            Assert.Equal(JsonValueKind.Number, scalar.ValueKind);
            Assert.Equal(expected, scalar.GetRawText());
            var request = $$"""{"scalar":{{json}},"wrapper":{{json}},"list":[{{json}}],"map":{"value":{{json}}},"nullable":{{json}},"optional":{{json}},"optionalNullable":{{json}}}""";
            var expectedRecord = $$"""{"scalar":{{expected}},"wrapper":{{expected}},"list":[{{expected}}],"map":{"value":{{expected}}},"nullable":{{expected}},"optional":{{expected}},"optionalNullable":{{expected}}}""";
            AssertJson(expectedRecord, await probe.RoundTrip("integers", request));
            AssertJson(expectedRecord, Assert.Single(await probe.Stream("integer-watch", request)));
        }
        foreach (var json in new[]
                 {
                     """{"scalar":0,"wrapper":0,"list":[],"map":{},"nullable":null}""",
                     """{"scalar":0,"wrapper":0,"list":[],"map":{},"nullable":null,"optional":0,"optionalNullable":null}""",
                 })
        {
            AssertJson(json, await probe.RoundTrip("integers", json));
            AssertJson(json, Assert.Single(await probe.Stream("integer-watch", json)));
        }
        Assert.Equal(cases.Length * 3 + 4, probe.Calls());
    }

    [Theory]
    [InlineData("byte", "-1", "256")]
    [InlineData("sbyte", "-129", "128")]
    [InlineData("short", "-32769", "32768")]
    [InlineData("ushort", "-1", "65536")]
    [InlineData("int", "-2147483649", "2147483648")]
    public async Task PrimitiveIntegers_RejectDescriptorValidValuesOutsideClrRangeWithJsonException(
        string clrType, string belowMinimum, string aboveMaximum)
    {
        using var probe = CompileProbe(clrType);
        foreach (var value in new[] { belowMinimum, aboveMaximum })
        foreach (var spelling in new[] { value, value + ".0", value + "e0" })
        foreach (var (method, json) in IntegerRequests(spelling))
        {
            using var document = JsonDocument.Parse(json);
            var descriptor = probe.Descriptor.Services.Single().FindMethod(method)!;
            // The existing descriptor integer domain is wider than the authored CLR type.
            Assert.True(probe.Descriptor.IsValid(descriptor.RequestSchemaReference, document.RootElement, out var error), error);
            var exception = await Assert.ThrowsAsync<JsonException>(() => probe.Invoke(method, document.RootElement).AsTask());
            Assert.IsType<OverflowException>(exception.InnerException);
            if (method == "integers")
            {
                var streamException = await Assert.ThrowsAsync<JsonException>(() => probe.Stream("integer-watch", json));
                Assert.IsType<OverflowException>(streamException.InnerException);
            }
        }
        Assert.Equal(0, probe.Calls());
    }

    [Theory]
    [InlineData("byte")]
    [InlineData("sbyte")]
    [InlineData("short")]
    [InlineData("ushort")]
    [InlineData("int")]
    public async Task PrimitiveIntegers_RejectFractionsCoercionsUnsafeIntegersAndNonfiniteLiteralsBeforeDispatch(string clrType)
    {
        using var probe = CompileProbe(clrType);
        foreach (var value in new[]
                 {
                     "1.5", "1e-1", "1.00000000000000000000001", "1e-400", "1e400",
                     "9007199254740992", "-9007199254740992", "\"1\"", "\"1.0\"", "\"1e0\"",
                     "\"NaN\"", "\"Infinity\"", "true", "false", "[]", "{}",
                 })
        foreach (var (method, json) in IntegerRequests(value))
        {
            using var document = JsonDocument.Parse(json);
            var descriptor = probe.Descriptor.Services.Single().FindMethod(method)!;
            Assert.False(probe.Descriptor.IsValid(descriptor.RequestSchemaReference, document.RootElement, out _));
            await Assert.ThrowsAsync<JsonException>(() => probe.Invoke(method, document.RootElement).AsTask());
            if (method == "integers")
            {
                await Assert.ThrowsAsync<JsonException>(() => probe.Stream("integer-watch", json, validateRequest: false));
            }
        }
        using var nullDocument = JsonDocument.Parse("null");
        await Assert.ThrowsAsync<JsonException>(() => probe.Invoke("integer", nullDocument.RootElement).AsTask());
        foreach (var literal in new[] { "NaN", "Infinity", "-Infinity" })
        {
            await Assert.ThrowsAnyAsync<JsonException>(() => probe.RoundTrip("integer", literal));
        }
        Assert.Equal(0, probe.Calls());
    }

    [Fact]
    public async Task PrimitiveIntegerRegistration_PreservesSdkLongBase64BigIntegerStringsBooleansAndFloats()
    {
        using var probe = CompileProbe("int");
        const string request = """{"wide":9007199254740991.0,"bytes":"AP8=","big":"9007199254740992000000000000000000000000","text":"1.0","flag":false,"single":1.25,"double":0.125}""";
        AssertJson(request.Replace("9007199254740991.0", "9007199254740991", StringComparison.Ordinal),
            await probe.RoundTrip("other-primitives", request));
        const string negative = """{"wide":-9007199254740991e0,"bytes":"","big":"-9007199254740992000000000000000000000000","text":"1e0","flag":false,"single":-0.0,"double":-0e0}""";
        const string expected = """{"wide":-9007199254740991,"bytes":"","big":"-9007199254740992000000000000000000000000","text":"1e0","flag":false,"single":0,"double":0}""";
        AssertJson(expected, await probe.RoundTrip("other-primitives", negative));
        var descriptor = probe.Descriptor.Services.Single().FindMethod("other-primitives")!;
        foreach (var invalid in new[]
                 {
                     request.Replace("9007199254740991.0", "9007199254740992", StringComparison.Ordinal),
                     request.Replace("9007199254740991.0", "1.5", StringComparison.Ordinal),
                     request.Replace("\"AP8=\"", "[0,255]", StringComparison.Ordinal),
                     request.Replace("AP8=", "AP8", StringComparison.Ordinal),
                     request.Replace("\"9007199254740992000000000000000000000000\"", "1", StringComparison.Ordinal),
                     request.Replace("1.25", "1e39", StringComparison.Ordinal),
                     request.Replace("0.125", "1e400", StringComparison.Ordinal),
                 })
        {
            using var document = JsonDocument.Parse(invalid);
            Assert.False(probe.Descriptor.IsValid(descriptor.RequestSchemaReference, document.RootElement, out _));
            await Assert.ThrowsAsync<JsonException>(() => probe.Invoke("other-primitives", document.RootElement).AsTask());
        }
        Assert.Equal(2, probe.Calls());
        await Assert.ThrowsAsync<JsonException>(() => probe.RoundTrip("invalid-wide-integer", request));
        Assert.Equal(3, probe.Calls());
    }

    [Fact]
    public async Task Enums_UseNumericValuesOrExplicitStringNamesAtRootAndInMembers()
    {
        using var probe = CompileProbe();
        foreach (var json in new[] { "-1", "0", "7" })
        {
            var result = await probe.RoundTrip("priority", json);
            Assert.Equal(JsonValueKind.Number, result.ValueKind);
            AssertJson(json, result);
        }
        foreach (var json in new[] { "\"ready-now\"", "\"Completed EXACT\"" })
        {
            var result = await probe.RoundTrip("status", json);
            Assert.Equal(JsonValueKind.String, result.ValueKind);
            AssertJson(json, result);
        }
        const string record = """{"priority":0,"status":"ready-now","nullablePriority":null,"selected":0,"values":[-1,0,7]}""";
        AssertJson(record, await probe.RoundTrip("enums", record));
        AssertJson(record, Assert.Single(await probe.Stream("enum-watch", record)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NumericEnums_AcceptAllSafeIntegerSpellingsWithoutRequestCanonicalization(bool includeRangeBounds)
    {
        using var probe = CompileProbe(includeRangeBounds: includeRangeBounds);
        (string Json, string Expected)[] cases =
        [
            ("0.0", "0"), ("-0e9", "0"), ("7e0", "7"), ("70e-1", "7"), ("7.000", "7"), ("-10e-1", "-1"),
        ];
        foreach (var (json, expected) in cases)
        {
            var result = await probe.RoundTrip("priority", json);
            Assert.Equal(expected, result.GetRawText());
        }
        const string request = """{"priority":0.0,"status":"ready-now","nullablePriority":7e0,"selected":-1.0,"values":[7.0,0e9,-1e0]}""";
        const string expectedRecord = """{"priority":0,"status":"ready-now","nullablePriority":7,"selected":-1,"values":[7,0,-1]}""";
        AssertJson(expectedRecord, await probe.RoundTrip("enums", request));
        AssertJson(expectedRecord, Assert.Single(await probe.Stream("enum-watch", request)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NumericEnums_RejectUndeclaredOrNonintegralReadsAndUndeclaredWrites(bool includeRangeBounds)
    {
        using var probe = CompileProbe(includeRangeBounds: includeRangeBounds);
        var method = probe.Descriptor.Services.Single().FindMethod("priority")!;
        foreach (var json in new[] { "1", "1.0", "7.5", "7e-1", "9007199254740992", "1e400", "\"7e0\"", "null" })
        {
            using var document = JsonDocument.Parse(json);
            Assert.False(probe.Descriptor.IsValid(method.RequestSchemaReference, document.RootElement, out _));
            var error = await Assert.ThrowsAsync<JsonException>(() => probe.Invoke("priority", document.RootElement).AsTask());
            if (json is "1" or "1.0")
            {
                Assert.StartsWith("The RPC numeric enum value is not declared.", error.Message, StringComparison.Ordinal);
            }
        }
        Assert.Equal(0, probe.Calls());
        var unary = await Assert.ThrowsAsync<JsonException>(() => probe.RoundTrip("invalid-priority", "0"));
        var stream = await Assert.ThrowsAsync<JsonException>(() => probe.Stream("invalid-priority-watch", "0"));
        Assert.StartsWith("The RPC numeric enum value is not declared.", unary.Message, StringComparison.Ordinal);
        Assert.StartsWith("The RPC numeric enum value is not declared.", stream.Message, StringComparison.Ordinal);
        Assert.Equal(2, probe.Calls());
    }

    [Fact]
    public async Task StringEnums_RoundTripExactEmptyWhitespaceUnicodeAndEscapedNames()
    {
        using var probe = CompileProbe();
        foreach (var value in new[] { "", " leading", "trailing ", "\t\r\n", "caf\u00e9\u96ea\U0001f680", "a\u0001\"\\b", "case", "Case" })
        {
            var json = JsonSerializer.Serialize(value);
            Assert.Equal(value, (await probe.RoundTrip("status", json)).GetString());
            var record = $$"""{"priority":0,"status":{{json}},"nullablePriority":null,"selected":0,"values":[]}""";
            AssertJson(record, await probe.RoundTrip("enums", record));
            AssertJson(record, Assert.Single(await probe.Stream("enum-watch", record)));
        }
    }

    [Fact]
    public async Task StringEnums_RejectCoercedOrUnknownReadsAndUndeclaredWrites()
    {
        using var probe = CompileProbe();
        var method = probe.Descriptor.Services.Single().FindMethod("status")!;
        foreach (var json in new[]
                 {
                     "\"leading\"", "\"trailing\"", "\"READY-NOW\"", "\"ready-now \"", "\"Ready\"", "\"0\"",
                     "\"class\"", "\" \"", "0", "null", "[]",
                 })
        {
            using var document = JsonDocument.Parse(json);
            Assert.False(probe.Descriptor.IsValid(method.RequestSchemaReference, document.RootElement, out _));
            var error = await Assert.ThrowsAsync<JsonException>(() => probe.Invoke("status", document.RootElement).AsTask());
            if (document.RootElement.ValueKind == JsonValueKind.String)
            {
                Assert.StartsWith("The RPC string enum value is not declared.", error.Message, StringComparison.Ordinal);
            }
        }
        Assert.Equal(0, probe.Calls());
        var unary = await Assert.ThrowsAsync<JsonException>(() => probe.RoundTrip("invalid-status", "\"ready-now\""));
        var stream = await Assert.ThrowsAsync<JsonException>(() => probe.Stream("invalid-status-watch", "\"ready-now\""));
        Assert.StartsWith("The RPC string enum value is not declared.", unary.Message, StringComparison.Ordinal);
        Assert.StartsWith("The RPC string enum value is not declared.", stream.Message, StringComparison.Ordinal);
        Assert.Equal(2, probe.Calls());
    }

    [Theory]
    [InlineData("initialized-optionals")]
    [InlineData("initialized-struct")]
    public async Task InitializedOptionals_RestoreAbsenceBeforePopulationButKeepPresentValues(string method)
    {
        using var probe = CompileProbe();
        foreach (var json in new[]
                 {
                     "{}",
                     """{"flag":false}""",
                     """{"flag":false,"count":0,"note":null}""",
                     """{"flag":true,"count":12,"note":"explicit"}""",
                 })
        {
            AssertJson(json, await probe.RoundTrip(method, json));
            AssertJson(json, Assert.Single(await probe.Stream(method + "-watch", json)));
        }
        AssertJson("""{"flag":false,"count":42,"note":null}""", await probe.RoundTrip(method + "-output", "{}"));
    }

    [Fact]
    public async Task PropertyNames_MatchFrozenSourceNamingAndExplicitJsonNames()
    {
        using var probe = CompileProbe();
        AssertJson(NamesJson, await probe.RoundTrip("names", NamesJson));
        var absent = JsonNode.Parse(NamesJson)!.AsObject();
        absent.Remove("optional-wire");
        AssertJson(absent.ToJsonString(), await probe.RoundTrip("names", absent.ToJsonString()));

        var combined = await probe.RoundTrip("combine", """{"urlValue":0,"userId":"id","event":false}""");
        AssertJson("""{"flag":false,"count":0,"text":"id","number":null}""", combined);
    }

    [Fact]
    public async Task Requests_RejectQuotedNumbersWrongNamesUnknownMembersAndWrongEnumRepresentationsBeforeDispatch()
    {
        using var probe = CompileProbe();
        (string Method, string Json)[] cases =
        [
            ("echo", """{"flag":false,"count":"0","text":null,"number":null}"""),
            ("echo", """{"Flag":false,"count":0,"text":null,"number":null}"""),
            ("echo", """{"count":0,"text":null,"number":null}"""),
            ("echo", """{"flag":false,"count":0,"text":null,"number":null,"extra":false}"""),
            ("echo", """{"flag":false,"count":0,"text":null,"number":null,"optionalCount":"0"}"""),
            ("flag", "0"),
            ("count", "\"0\""),
            ("list", """{"value":[]}"""),
            ("map", """{"zero":"0"}"""),
            ("priority", "\"Zero\""),
            ("priority", "\"0\""),
            ("status", "0"),
            ("status", "\"Ready\""),
            ("content", """{"value":{}}"""),
            ("names", NamesJson.Replace("\"userId\"", "\"user__id\"", StringComparison.Ordinal)),
            ("names", NamesJson.Replace("\"urlValue\"", "\"URLValue\"", StringComparison.Ordinal)),
            ("names", NamesJson.Replace("\"Wire_Name\"", "\"wire_Name\"", StringComparison.Ordinal)),
        ];
        foreach (var (method, json) in cases)
        {
            using var document = JsonDocument.Parse(json);
            var descriptor = probe.Descriptor.Services.Single().FindMethod(method)!;
            Assert.False(probe.Descriptor.IsValid(descriptor.RequestSchemaReference, document.RootElement, out _));
            await Assert.ThrowsAsync<JsonException>(() => probe.Invoke(method, document.RootElement).AsTask());
            if (method == "echo")
            {
                await Assert.ThrowsAsync<JsonException>(() => probe.Stream("watch", json, validateRequest: false));
            }
        }
        Assert.Equal(0, probe.Calls());
    }

    [Fact]
    public void NullableOuterAroundNullableUnderlyingValue_RetainsTheProfilesAmbiguousNullRejection()
    {
        var compiler = new SunderRpcCSharpSourceCompiler();
        var input = new SunderRpcCSharpCompilerInput("example.codecs", "1.0.0", [new("Nullable.cs", Source)], References());
        var valid = compiler.Compile(input);
        Assert.True(valid.Success, string.Join(Environment.NewLine, valid.Diagnostics));
        var source = Source.Replace("public required NullText NullText", "public required NullText? NullText", StringComparison.Ordinal);
        var result = compiler.Compile(input with { Sources = [new("Nullable.cs", source)] });
        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("rpc-authoring.value-invalid", diagnostic.Id);
        Assert.Equal("Nullable.cs", diagnostic.Path);
        Assert.Contains("branch that accepts null", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public sealed class Bad { private Bad() { } public required string Value { get; init; } }", "accessible constructor callable without arguments")]
    [InlineData("public sealed class Bad { public Bad(string seed) { } public required string Value { get; init; } }", "accessible constructor callable without arguments")]
    [InlineData("public abstract class Bad { public required string Value { get; init; } }", "must be concrete")]
    [InlineData("public sealed class Bad { public required string Value { private get; init; } }", "accessible Value getter")]
    [InlineData("public sealed record Bad { public required string Other { get; init; } }", "exactly one required public Value")]
    public void InvalidTransparentWrappers_ReportSourceDiagnosticsInsteadOfBrokenGeneratedCode(string declaration, string message)
    {
        var source = $$"""
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            [SunderRpcService(SunderRpcExposure.Package)]
            public sealed partial class InvalidService
            {
                [SunderRpcMethod]
                public ValueTask<Bad> EchoAsync(Bad request) => ValueTask.FromResult(request);
            }
            [SunderRpcTransparent]
            {{declaration}}
            """;
        var result = new SunderRpcCSharpSourceCompiler().Compile(new(
            "example.codecs", "1.0.0", [new("Invalid.cs", source)], References()));
        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("rpc-authoring.value-invalid", diagnostic.Id);
        Assert.Contains(message, diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("Invalid.cs", diagnostic.Path);
        Assert.NotNull(diagnostic.Line);
    }

    [Theory]
    [InlineData("public sealed class Bad { public Bad(SunderRpcOptional<bool> flag) { Flag = flag; } public SunderRpcOptional<bool> Flag { get; init; } = new(false); }", "parameterless JSON constructor")]
    [InlineData("public sealed class Bad { public Bad() { } [JsonConstructor] public Bad(SunderRpcOptional<bool> flag) { Flag = flag; } public SunderRpcOptional<bool> Flag { get; init; } = new(false); }", "parameterless JSON constructor")]
    [InlineData("public sealed class Bad { [JsonConstructor] public Bad(SunderRpcOptional<bool> flag = default) { Flag = flag; } public SunderRpcOptional<bool> Flag { get; init; } }", "parameterless JSON constructor")]
    [InlineData("public readonly record struct Bad { [JsonConstructor] public Bad(SunderRpcOptional<bool> flag) { Flag = flag; } public SunderRpcOptional<bool> Flag { get; init; } }", "parameterless JSON constructor")]
    [InlineData("public sealed class Bad { public SunderRpcOptional<bool> Flag { get; } = new(false); }", "JSON-readable getter and writable setter")]
    public void OptionalConstruction_RejectsShapesThatCannotResetAbsenceBeforePopulation(string declaration, string message)
    {
        var source = $$"""
            using System.Text.Json.Serialization;
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            [SunderRpcService(SunderRpcExposure.Package)]
            public sealed partial class InvalidService
            {
                [SunderRpcMethod]
                public ValueTask<Bad> EchoAsync(Bad request) => ValueTask.FromResult(request);
            }
            {{declaration}}
            """;
        var result = new SunderRpcCSharpSourceCompiler().Compile(new(
            "example.codecs", "1.0.0", [new("Construction.cs", source)], References()));
        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("rpc-authoring.value-invalid", diagnostic.Id);
        Assert.Contains(message, diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("Construction.cs", diagnostic.Path);
    }

    [Fact]
    public void StringEnumAliases_RejectClrValuesThatCannotDistinguishExplicitWireNames()
    {
        var source = Source.Replace(
            "[JsonStringEnumMemberName(\"Completed EXACT\")] Completed,",
            "[JsonStringEnumMemberName(\"Completed EXACT\")] Completed = 0,", StringComparison.Ordinal);
        var result = new SunderRpcCSharpSourceCompiler().Compile(new(
            "example.codecs", "1.0.0", [new("Alias.cs", source)], References()));
        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("rpc-authoring.value-invalid", diagnostic.Id);
        Assert.Contains("unique CLR values", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("Alias.cs", diagnostic.Path);
    }

    private static (string Method, string Json)[] IntegerRequests(string value)
        =>
        [
            ("integer", value),
            ("integers", $$"""{"scalar":{{value}},"wrapper":0,"list":[],"map":{},"nullable":null}"""),
            ("integers", $$"""{"scalar":0,"wrapper":{{value}},"list":[],"map":{},"nullable":null}"""),
            ("integers", $$"""{"scalar":0,"wrapper":0,"list":[{{value}}],"map":{},"nullable":null}"""),
            ("integers", $$"""{"scalar":0,"wrapper":0,"list":[],"map":{"value":{{value}}},"nullable":null}"""),
            ("integers", $$"""{"scalar":0,"wrapper":0,"list":[],"map":{},"nullable":{{value}}}"""),
            ("integers", $$"""{"scalar":0,"wrapper":0,"list":[],"map":{},"nullable":null,"optional":{{value}}}"""),
            ("integers", $$"""{"scalar":0,"wrapper":0,"list":[],"map":{},"nullable":null,"optionalNullable":{{value}}}"""),
        ];

    private static RuntimeProbe CompileProbe(string? integerType = null, bool includeRangeBounds = true)
    {
        var sourceText = integerType is null ? Source : Source + "\n" + IntegerSource.Replace("INTEGER", integerType, StringComparison.Ordinal);
        if (!includeRangeBounds) sourceText = sourceText.Replace("[SunderRpcNumericEnum]", "[SunderRpcNumericEnum(IncludeRangeBounds = false)]", StringComparison.Ordinal);
        var compiler = new SunderRpcCSharpSourceCompiler();
        var input = new SunderRpcCSharpCompilerInput("example.codecs", "9.8.7", [new("Codec.cs", sourceText)], References());
        var first = compiler.Compile(input);
        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics));
        var contract = Assert.Single(first.Contracts);
        var generated = SunderRpcCSharpImplementationEmitter.Emit(first.Contracts);
        Assert.Equal(Encoding.UTF8.GetBytes(generated), Encoding.UTF8.GetBytes(SunderRpcCSharpImplementationEmitter.Emit(first.Contracts)));
        var second = compiler.Compile(input with
        {
            SupplementalSources = [new("authoring.g.cs", generated)],
            RequireGeneratedMembersResolved = true,
        });
        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics));
        var verified = Assert.Single(second.Contracts);
        Assert.Equal(contract.Descriptor.GetCanonicalUtf8(), verified.Descriptor.GetCanonicalUtf8());
        Assert.Equal(contract.Descriptor.GetCanonicalAbiProjectionUtf8(), verified.Descriptor.GetCanonicalAbiProjectionUtf8());
        Assert.Equal(contract.Providers.Select(provider => provider.ProviderId), verified.Providers.Select(provider => provider.ProviderId));
        Assert.Equal(Encoding.UTF8.GetBytes(generated), Encoding.UTF8.GetBytes(SunderRpcCSharpImplementationEmitter.Emit(second.Contracts)));

        var compilation = CSharpCompilation.Create("ImplementationCodecs_" + Guid.NewGuid().ToString("N"),
            new SunderRpcCSharpSourceFile[] { new("Codec.cs", sourceText), new("authoring.g.cs", generated) }
                .Select(source => CSharpSyntaxTree.ParseText(source.Text, new CSharpParseOptions(LanguageVersion.CSharp14), source.Path)),
            References().Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable, generalDiagnosticOption: ReportDiagnostic.Error));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        stream.Position = 0;
        var context = new AssemblyLoadContext(compilation.AssemblyName, isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Build");
            return new(context, assembly, contract.Descriptor);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private static string[] References()
        => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(SunderRpcServiceAttribute).Assembly.Location)
            .Append(typeof(SunderWorkerProviderRegistration).Assembly.Location)
            .Distinct(StringComparer.Ordinal).ToArray();

    private static void AssertJson(string expected, JsonElement actual)
        => Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual.GetRawText())),
            $"Expected {expected}; actual {actual.GetRawText()}");

    private sealed class RuntimeProbe : IDisposable
    {
        private readonly AssemblyLoadContext _loadContext;
        private readonly SunderWorkerProviderRegistration _registration;
        private readonly SunderRpcInvocationContext _context;

        public RuntimeProbe(AssemblyLoadContext loadContext, Assembly assembly, SunderRpcContractDescriptor descriptor)
        {
            _loadContext = loadContext;
            Descriptor = descriptor;
            var probe = assembly.GetType("Example.Codecs.Probe")!;
            _registration = probe.GetMethod("Create")!.CreateDelegate<Func<SunderWorkerProviderRegistration>>()();
            Calls = probe.GetMethod("Calls")!.CreateDelegate<Func<int>>();
            NullableOutersWereNull = probe.GetMethod("NullableOutersWereNull")!.CreateDelegate<Func<bool>>();
            Assert.Equal("example.codecs.codec.provider", _registration.ProviderId);
            Assert.Equal(descriptor.ContractId, _registration.ContractId);
            Assert.Equal(descriptor.Version, _registration.ContractVersion);
            Assert.Equal(descriptor.Sha256, _registration.DescriptorSha256);
            Assert.Equal(descriptor.AbiSha256, _registration.AbiSha256);
            Assert.True(loadContext.IsCollectible);
            // The invocation context is host-created; only its nonpublic construction needs reflection.
            _context = (SunderRpcInvocationContext)Activator.CreateInstance(typeof(SunderRpcInvocationContext),
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                args:
                [
                    "example.caller", "1.0.0",
                    new SunderRpcProviderSnapshot("example.codecs", "9.8.7", _registration.ProviderId,
                        descriptor.ContractId, descriptor.Version, descriptor.Sha256, descriptor.AbiSha256,
                        Guid.NewGuid(), 1, 1, new("codec-endpoint"), 1, SunderRpcProviderState.Active),
                    DateTimeOffset.MaxValue, 1, CancellationToken.None, null,
                ], culture: null)!;
        }

        public SunderRpcContractDescriptor Descriptor { get; }
        public Func<int> Calls { get; }
        public Func<bool> NullableOutersWereNull { get; }

        // Broker cloning preserves the incoming number spelling rather than canonicalizing it.
        public ValueTask<JsonElement> Invoke(string method, JsonElement request)
            => _registration.Handler.InvokeUnaryAsync(_context, Descriptor.Services.Single().ServiceId, method, request.Clone());

        public async Task<JsonElement> RoundTrip(string method, string json)
        {
            using var document = JsonDocument.Parse(json);
            var descriptor = Descriptor.Services.Single().FindMethod(method)!;
            Assert.True(Descriptor.IsValid(descriptor.RequestSchemaReference, document.RootElement, out var requestError), requestError);
            var result = await Invoke(method, document.RootElement);
            Assert.True(Descriptor.IsValid(descriptor.OutputSchemaReference, result, out var responseError), responseError);
            return result;
        }

        public async Task<List<JsonElement>> Stream(string method, string json, bool validateRequest = true)
        {
            using var document = JsonDocument.Parse(json);
            var service = Descriptor.Services.Single();
            var descriptor = service.FindMethod(method)!;
            if (validateRequest)
            {
                Assert.True(Descriptor.IsValid(descriptor.RequestSchemaReference, document.RootElement, out var error), error);
            }
            var events = new List<JsonElement>();
            await foreach (var item in _registration.Handler.InvokeServerStreamAsync(_context, service.ServiceId, method, document.RootElement.Clone()))
            {
                Assert.True(Descriptor.IsValid(descriptor.OutputSchemaReference, item, out var error), error);
                events.Add(item);
            }
            return events;
        }

        public void Dispose() => _loadContext.Unload();
    }

    private const string DefaultsJson = """{"flag":false,"count":0,"text":null,"number":null}""";
    private const string NamesJson = """{"event":false,"urlValue":0,"userId":"id","Wire_Name":"key","getUrl":"url","user_ID":false,"camelCase":"camel","camel_Case":false,"line\n\"\\name":false,"optional-wire":null}""";
    private const string ContentJson = """{"id":"content-1","length":0,"sha256":"0000000000000000000000000000000000000000000000000000000000000000","mediaType":"application/octet-stream","fileName":"","expiresAtUtc":"2030-01-01T00:00:00Z","repeatability":"single-use"}""";

    private const string IntegerSource = """
        public sealed partial class CodecService
        {
            [SunderRpcMethod]
            public ValueTask<IntegerValue> IntegerAsync(IntegerValue request) => Respond(new IntegerValue { Value = request.Value });
            [SunderRpcMethod]
            public ValueTask<IntegerRecord> IntegersAsync(IntegerRecord request) => Respond(request);
            [SunderRpcMethod]
            public async IAsyncEnumerable<IntegerRecord> IntegerWatchAsync(IntegerRecord request)
            {
                yield return await IntegersAsync(request);
            }
            [SunderRpcMethod]
            public ValueTask<OtherPrimitives> OtherPrimitivesAsync(OtherPrimitives request) => Respond(request);
            [SunderRpcMethod]
            public ValueTask<OtherPrimitives> InvalidWideIntegerAsync(OtherPrimitives request) => Respond(request with { Wide = long.MaxValue });
        }
        [SunderRpcTransparent]
        public readonly record struct IntegerValue { public required INTEGER Value { get; init; } }
        public sealed record IntegerRecord
        {
            public required INTEGER Scalar { get; init; }
            public required IntegerValue Wrapper { get; init; }
            public required IReadOnlyList<INTEGER> List { get; init; }
            public required IReadOnlyDictionary<string, INTEGER> Map { get; init; }
            public required INTEGER? Nullable { get; init; }
            public SunderRpcOptional<INTEGER> Optional { get; init; }
            public SunderRpcOptional<INTEGER?> OptionalNullable { get; init; }
        }
        public sealed record OtherPrimitives
        {
            public required long Wide { get; init; }
            public required byte[] Bytes { get; init; }
            public required System.Numerics.BigInteger Big { get; init; }
            public required string Text { get; init; }
            public required bool Flag { get; init; }
            public required float Single { get; init; }
            public required double Double { get; init; }
        }
        """;

    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Text.Json;
        using System.Text.Json.Serialization;
        using System.Threading.Tasks;
        using Sunder.Sdk.Rpc;
        using Sunder.Sdk.Worker;
        namespace Example.Codecs;
        public static class Probe
        {
            private static readonly CodecService Service = new();
            public static SunderWorkerProviderRegistration Create() => Service.CreateSunderRpcProviderRegistration();
            public static int Calls() => Service.Calls;
            public static bool NullableOutersWereNull() => Service.NullableOutersWereNull;
        }
        [SunderRpcService(SunderRpcExposure.Package)]
        public sealed partial class CodecService
        {
            public int Calls { get; private set; }
            public bool NullableOutersWereNull { get; private set; }
            private ValueTask<T> Respond<T>(T response)
            {
                Calls++;
                return ValueTask.FromResult(response);
            }
            [SunderRpcMethod]
            public ValueTask<Defaults> EchoAsync(Defaults request) => Respond(request);
            [SunderRpcMethod]
            public async IAsyncEnumerable<Defaults> WatchAsync(Defaults request)
            {
                Calls++;
                await Task.CompletedTask;
                yield return request;
                yield return new Defaults
                {
                    Flag = false, Count = 0, Text = null, Number = null,
                    OptionalFlag = new(false), OptionalCount = new(0), OptionalText = new(null),
                };
            }
            [SunderRpcMethod]
            public ValueTask<FlagValue> FlagAsync(FlagValue request) => Respond(new FlagValue { Value = request.Value });
            [SunderRpcMethod]
            public ValueTask<CountValue> CountAsync(CountValue request) => Respond(new CountValue { Value = request.Value });
            [SunderRpcMethod]
            public ValueTask<TextValue> TextAsync(TextValue request) => Respond(new TextValue { Value = request.Value });
            [SunderRpcMethod]
            public ValueTask<TextList> ListAsync(TextList request) => Respond(new TextList { Value = request.Value });
            [SunderRpcMethod]
            public ValueTask<CountMap> MapAsync(CountMap request) => Respond(new CountMap { Value = request.Value });
            [SunderRpcMethod]
            public ValueTask<NullText> NullTextAsync(NullText request) => Respond(new NullText { Value = request.Value });
            [SunderRpcMethod]
            public ValueTask<WrappedNullText> WrappedNullTextAsync(WrappedNullText request)
                => Respond(new WrappedNullText { Value = request.Value with { } });
            [SunderRpcMethod]
            public ValueTask<NullCount> NullCountAsync(NullCount request) => Respond(new NullCount { Value = request.Value });
            [SunderRpcMethod]
            public ValueTask<ContentValue> ContentAsync(ContentValue request) => Respond(new ContentValue { Value = request.Value with { } });
            [SunderRpcMethod]
            public ValueTask<AnyValue> AnyAsync(AnyValue request) => Respond(new AnyValue { Value = request.Value.Clone() });
            [SunderRpcMethod]
            public ValueTask<WrappedRecord> NestedAsync(WrappedRecord request)
            {
                _ = request.Label.Value.Length;
                _ = request.Content.Value.Id;
                _ = request.NullText.Value;
                NullableOutersWereNull = request.NullableLabel is null && !request.NullableCount.HasValue;
                foreach (var item in request.Items.Value) _ = item?.Value.Length;
                foreach (var item in request.Scores.Value) _ = item.Value.Value;
                return Respond(request);
            }
            [SunderRpcMethod]
            public async IAsyncEnumerable<WrappedRecord> WrappedWatchAsync(WrappedRecord request)
            {
                yield return await NestedAsync(request);
            }
            [SunderRpcMethod]
            public ValueTask<Priority> PriorityAsync(Priority request) => Respond(request);
            [SunderRpcMethod]
            public ValueTask<Status> StatusAsync(Status request) => Respond(request);
            [SunderRpcMethod]
            public ValueTask<EnumRecord> EnumsAsync(EnumRecord request) => Respond(request);
            [SunderRpcMethod]
            public async IAsyncEnumerable<EnumRecord> EnumWatchAsync(EnumRecord request)
            {
                yield return await EnumsAsync(request);
            }
            [SunderRpcMethod]
            public ValueTask<Priority> InvalidPriorityAsync(Priority request) => Respond((Priority)42);
            [SunderRpcMethod]
            public async IAsyncEnumerable<Priority> InvalidPriorityWatchAsync(Priority request)
            {
                yield return await InvalidPriorityAsync(request);
            }
            [SunderRpcMethod]
            public ValueTask<Status> InvalidStatusAsync(Status request) => Respond((Status)42);
            [SunderRpcMethod]
            public async IAsyncEnumerable<Status> InvalidStatusWatchAsync(Status request)
            {
                yield return await InvalidStatusAsync(request);
            }
            [SunderRpcMethod]
            public ValueTask<InitializedOptionals> InitializedOptionalsAsync(InitializedOptionals request) => Respond(request);
            [SunderRpcMethod]
            public async IAsyncEnumerable<InitializedOptionals> InitializedOptionalsWatchAsync(InitializedOptionals request)
            {
                yield return await InitializedOptionalsAsync(request);
            }
            [SunderRpcMethod]
            public ValueTask<InitializedOptionals> InitializedOptionalsOutputAsync() => Respond(new InitializedOptionals());
            [SunderRpcMethod]
            public ValueTask<InitializedStruct> InitializedStructAsync(InitializedStruct request) => Respond(request);
            [SunderRpcMethod]
            public async IAsyncEnumerable<InitializedStruct> InitializedStructWatchAsync(InitializedStruct request)
            {
                yield return await InitializedStructAsync(request);
            }
            [SunderRpcMethod]
            public ValueTask<InitializedStruct> InitializedStructOutputAsync() => Respond(new InitializedStruct());
            [SunderRpcMethod]
            public ValueTask<Names> NamesAsync(Names request) => Respond(request);
            [SunderRpcMethod]
            public ValueTask<Defaults> CombineAsync(int URLValue, string user_ID, bool @event)
                => Respond(new Defaults { Flag = @event, Count = URLValue, Text = user_ID, Number = null });
        }
        public sealed record Defaults
        {
            public required bool Flag { get; init; }
            public required int Count { get; init; }
            public required string? Text { get; init; }
            public required int? Number { get; init; }
            public SunderRpcOptional<bool> OptionalFlag { get; init; }
            public SunderRpcOptional<int> OptionalCount { get; init; }
            public SunderRpcOptional<string?> OptionalText { get; init; }
        }
        public sealed class InitializedOptionals
        {
            public InitializedOptionals() { Count = new(42); }
            public InitializedOptionals(SunderRpcOptional<bool> flag) { Flag = flag; }
            public SunderRpcOptional<bool> Flag { get; init; } = new(false);
            public SunderRpcOptional<int> Count { get; init; } = new(7);
            [JsonInclude]
            public SunderRpcOptional<string?> Note { get; private init; } = new(null);
        }
        public readonly record struct InitializedStruct
        {
            [JsonConstructor]
            public InitializedStruct() { Count = new(42); }
            public SunderRpcOptional<bool> Flag { get; init; } = new(false);
            public SunderRpcOptional<int> Count { get; init; } = new(7);
            public SunderRpcOptional<string?> Note { get; init; } = new(null);
        }
        [SunderRpcTransparent]
        public sealed class FlagValue
        {
            internal FlagValue(bool unused = false) { }
            public required bool Value { get; set; }
        }
        [SunderRpcTransparent]
        public readonly record struct CountValue { public required int Value { get; init; } }
        [SunderRpcTransparent]
        public sealed record TextValue { public required string Value { get; init; } }
        [SunderRpcTransparent]
        public sealed record TextList { public required IReadOnlyList<TextValue?> Value { get; init; } }
        [SunderRpcTransparent]
        public sealed record CountMap { public required IReadOnlyDictionary<string, CountValue> Value { get; init; } }
        [SunderRpcTransparent]
        public sealed record NullText { public required string? Value { get; init; } }
        [SunderRpcTransparent]
        public sealed record WrappedNullText { public required NullText Value { get; init; } }
        [SunderRpcTransparent]
        public readonly record struct NullCount { public required int? Value { get; init; } }
        [SunderRpcTransparent]
        public sealed record ContentValue { public required SunderRpcContentReference Value { get; init; } }
        [SunderRpcTransparent]
        public sealed record AnyValue { public required JsonElement Value { get; init; } }
        public sealed record WrappedRecord
        {
            public required TextValue Label { get; init; }
            public required CountValue Count { get; init; }
            public required FlagValue Flag { get; init; }
            public required TextList Items { get; init; }
            public required CountMap Scores { get; init; }
            public required TextValue? NullableLabel { get; init; }
            public required CountValue? NullableCount { get; init; }
            public required NullText NullText { get; init; }
            public required NullCount NullCount { get; init; }
            public required ContentValue Content { get; init; }
            public SunderRpcOptional<TextValue?> OptionalLabel { get; init; }
            public SunderRpcOptional<CountValue> OptionalCount { get; init; }
            public required IReadOnlyList<Defaults> Records { get; init; }
            public required IReadOnlyDictionary<string, Defaults> RecordsByKey { get; init; }
        }
        [SunderRpcNumericEnum]
        public enum Priority : long { Negative = -1, Zero = 0, Seven = 7 }
        public enum Status
        {
            [JsonStringEnumMemberName("ready-now")] Ready,
            [JsonStringEnumMemberName("Completed EXACT")] Completed,
            [JsonStringEnumMemberName("")] @class,
            [JsonStringEnumMemberName(" leading")] Leading,
            [JsonStringEnumMemberName("trailing ")] Trailing,
            [JsonStringEnumMemberName("\t\r\n")] Whitespace,
            [JsonStringEnumMemberName("caf\u00e9\u96ea\U0001f680")] Unicode,
            [JsonStringEnumMemberName("a\u0001\"\\b")] @default,
            [JsonStringEnumMemberName("case")] @event,
            [JsonStringEnumMemberName("Case")] UpperCase,
        }
        public sealed record EnumRecord
        {
            public required Priority Priority { get; init; }
            public required Status Status { get; init; }
            public required Priority? NullablePriority { get; init; }
            public SunderRpcOptional<Priority> Selected { get; init; }
            public required IReadOnlyList<Priority> Values { get; init; }
        }
        public sealed record Names
        {
            public required bool @event { get; init; }
            public required int URLValue { get; init; }
            public required string user_ID { get; init; }
            [JsonPropertyName("Wire_Name")]
            public required string APIKey { get; init; }
            public required string GetURL { get; init; }
            [JsonPropertyName("user_ID")]
            public required bool RawCollision { get; init; }
            public required string Camel_Case { get; init; }
            [JsonPropertyName("camel_Case")]
            public required bool CamelCollision { get; init; }
            [JsonPropertyName("line\n\"\\name")]
            public required bool Escaped { get; init; }
            [JsonPropertyName("optional-wire")]
            public SunderRpcOptional<string?> @params { get; init; }
        }
        """;
}
