using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpImplementationCodecTests
{
    [Fact]
    public async Task RequiredRecordMetadataPreservesSelectedConstructorNormalization()
    {
        using var probe = CompileProbe(additionalSource: """
            public sealed record RequiredNormalizedRecord {
                [System.Text.Json.Serialization.JsonConstructor, System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
                public RequiredNormalizedRecord(string name) { Name = name.ToUpperInvariant(); }
                public required string Name { get; init; }
            }
            public sealed partial class CodecService {
                public ValueTask<RequiredNormalizedRecord> RequiredNormalizedAsync(RequiredNormalizedRecord request) => Respond(request);
            }
            """);
        AssertJson("""{"name":"NORMALIZED"}""", await probe.RoundTrip("required-normalized", """{"name":"normalized"}"""));
        using var missing = JsonDocument.Parse("{}");
        await Assert.ThrowsAsync<JsonException>(() => probe.Invoke("required-normalized", missing.RootElement).AsTask());
        Assert.Equal(1, probe.Calls());
    }

    [Fact]
    public async Task ConstructorValues_NormalizingNullableInputsKeepRequiredNonNullPropertyWireTypes()
    {
        using var probe = CompileProbe(additionalSource: """
            public sealed record Workspace(IReadOnlyList<PathRecord>? Paths = null, IReadOnlyList<PathRecord>? Documents = null, string? Name = null)
            {
                public IReadOnlyList<PathRecord> Paths { get; init; } = Paths ?? [];
                public IReadOnlyList<PathRecord> Documents { get; init; } = Documents ?? [];
                public string Name { get; init; } = Name ?? "local";
            }
            public sealed record PathRecord(string Id, DateTimeOffset CreatedAt);
            public sealed partial class CodecService
            {
                public ValueTask<Workspace> WorkspaceAsync(Workspace request) => Respond(request with { });
                public ValueTask<Workspace> DefaultWorkspaceAsync() => Respond(new Workspace());
                public async IAsyncEnumerable<Workspace> WorkspacesAsync(Workspace request) { yield return await WorkspaceAsync(request); }
            }
            """);
        const string empty = """{"paths":[],"documents":[],"name":"local"}""";
        const string populated = """{"paths":[{"id":"path","createdAt":"2026-10-06T12:34:56.1234567+05:45"}],"documents":[{"id":"doc","createdAt":"2026-10-06T12:34:56-03:30"}],"name":"remote"}""";
        AssertJson(empty, await probe.RoundTrip("default-workspace", "{}"));
        foreach (var json in new[] { empty, populated })
        {
            AssertJson(json, await probe.RoundTrip("workspace", json));
            AssertJson(json, Assert.Single(await probe.Stream("workspaces", json)));
        }
        var schema = probe.Descriptor.Definitions["Workspace"];
        Assert.Equal(new[] { "documents", "name", "paths" }, schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal("array", schema.GetProperty("properties").GetProperty("paths").GetProperty("type").GetString());
        foreach (var member in new[] { "paths", "documents", "name" })
        foreach (var absent in new[] { false, true })
        {
            var invalid = JsonNode.Parse(empty)!.AsObject();
            if (absent) invalid.Remove(member); else invalid[member] = null;
            using var input = JsonDocument.Parse(invalid.ToJsonString());
            Assert.False(probe.Descriptor.IsValid("#/$defs/Workspace", input.RootElement, out _));
            await Assert.ThrowsAsync<JsonException>(() => probe.Invoke("workspace", input.RootElement).AsTask());
        }
        Assert.Equal(5, probe.Calls());
    }

    [Fact]
    public async Task OrdinaryContext_LeadingInvocationContextStaysOutOfTheWireRequest()
    {
        using var probe = CompileProbe(additionalSource: """
            public sealed partial class CodecService
            {
                public ValueTask<Defaults> ContextFirstAsync(SunderRpcInvocationContext context, Defaults request, System.Threading.CancellationToken token = default)
                {
                    if (context is null) throw new Exception("Host invocation context was lost.");
                    token.ThrowIfCancellationRequested();
                    return Respond(request);
                }
                public async IAsyncEnumerable<Defaults> ContextFirstWatchAsync(SunderRpcInvocationContext context, Defaults request,
                    [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken token = default)
                { yield return await ContextFirstAsync(context, request, token); }
            }
            """);
        AssertJson(DefaultsJson, await probe.RoundTrip("context-first", DefaultsJson));
        AssertJson(DefaultsJson, Assert.Single(await probe.Stream("context-first-watch", DefaultsJson)));
        Assert.Equal("#/$defs/Defaults", probe.Descriptor.Services.Single().FindMethod("context-first")!.RequestSchemaReference);
        Assert.Equal(2, probe.Calls());
    }

    [Fact]
    public async Task ConstructorValues_PreserveRequiredDefaultsNullableMembersAndExplicitPresence()
    {
        using var probe = CompileProbe(additionalSource: ConstructorSource);
        const string json = """{"id":"profile","description":null,"createdAt":"2026-10-06T12:34:56.1234567+05:45","updatedAt":null,"bindings":[{"name":"chat","timestamps":[null,"2026-10-06T01:02:03-03:30"]}],"isInternal":false,"mode":1,"link":"local"}""";
        AssertJson(json, await probe.RoundTrip("profile", json));
        AssertJson(json, Assert.Single(await probe.Stream("profile-watch", json)));
        var shape = probe.Descriptor.Definitions["Profile"];
        Assert.Equal(new[] { "bindings", "createdAt", "description", "id", "isInternal", "link", "mode", "updatedAt" },
            shape.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));
        foreach (var member in shape.GetProperty("required").EnumerateArray())
        {
            var missing = JsonNode.Parse(json)!.AsObject(); missing.Remove(member.GetString()!);
            using var input = JsonDocument.Parse(missing.ToJsonString());
            Assert.False(probe.Descriptor.IsValid("#/$defs/Profile", input.RootElement, out _));
            await Assert.ThrowsAsync<JsonException>(() => probe.Invoke("profile", input.RootElement).AsTask());
        }
        foreach (var input in new[] { """{"id":"p"}""", """{"id":"p","note":null}""", """{"id":"p","note":"present"}""" })
            AssertJson(input, await probe.RoundTrip("optional-profile", input));
        Assert.Equal(5, probe.Calls());
        AssertJson("""{"id":"p","mode":1}""", await probe.RoundTrip("record-struct", """{"id":"p","mode":1}"""));
    }

    [Theory]
    [InlineData("public sealed class Constructed { public Constructed(SunderRpcOptional<bool> flag) { Flag = flag; } public SunderRpcOptional<bool> Flag { get; init; } = new(true); }")]
    [InlineData("public sealed class Constructed { public Constructed() => throw new Exception(); [JsonConstructor] public Constructed(SunderRpcOptional<bool> flag) { Flag = new(true); } public SunderRpcOptional<bool> Flag { get; init; } }")]
    [InlineData("public sealed class Constructed { [JsonConstructor] internal Constructed(SunderRpcOptional<bool> flag = default) { Flag = new(true); } public SunderRpcOptional<bool> Flag { get; init; } }")]
    [InlineData("public readonly record struct Constructed { [JsonConstructor] public Constructed(SunderRpcOptional<bool> flag) { Flag = flag; } public SunderRpcOptional<bool> Flag { get; init; } }")]
    [InlineData("public sealed record Constructed([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] SunderRpcOptional<bool> Flag = default);")]
    [InlineData("public sealed class Constructed { [JsonIgnore, SunderServiceMember] public SunderRpcOptional<bool> Flag { get; init; } = new(true); }")]
    [InlineData("public readonly record struct Constructed { public Constructed() { Flag = new(true); } [JsonIgnore, SunderServiceMember] public SunderRpcOptional<bool> Flag { get; init; } }")]
    public async Task ConstructorValues_OptionalPresenceOverridesConstructionInitializers(string declaration)
    {
        using var probe = CompileProbe(additionalSource: declaration + """

            public sealed partial class CodecService
            {
                public ValueTask<Constructed> ConstructedAsync(Constructed request) => Respond(request);
            }
            """);
        foreach (var json in new[] { "{}", """{"flag":false}""", """{"flag":true}""" })
            AssertJson(json, await probe.RoundTrip("constructed", json));
    }

    [Fact]
    public async Task ConstructorValues_OptionalJsonOmissionPreservesAbsentNullAndFalse()
    {
        using var probe = CompileProbe(additionalSource: """
            public sealed record Chat([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] SunderRpcOptional<JsonElement> Json = default);
            public sealed partial class CodecService { public ValueTask<Chat> ChatAsync(Chat request) => Respond(request); }
            """);
        foreach (var json in new[] { "{}", "{\"json\":null}", "{\"json\":false}", "{\"json\":{\"value\":0}}" })
            AssertJson(json, await probe.RoundTrip("chat", json));
    }

    [Fact]
    public async Task ConstructorValues_IgnoredSecretsRemainLocalAndDefaultsArePreserved()
    {
        using var probe = CompileProbe(additionalSource: """
            public sealed record SecretValue(string Id, [property: JsonIgnore] string Secret = "constructor-default")
            {
                [JsonIgnore] public string Local { get; init; } = "initializer-default";
                [JsonIgnore] internal Action? DispatchStarted { get; init; } = () => { };
            }
            public sealed partial class CodecService
            {
                public ValueTask<SecretValue> SecretsAsync(SecretValue request)
                {
                    if (request.Secret != "constructor-default" || request.Local != "initializer-default" || request.DispatchStarted is null)
                        throw new Exception("Excluded state lost its authored construction defaults.");
                    return Respond(request);
                }
            }
            """);
        AssertJson("{\"id\":\"safe\"}", await probe.RoundTrip("secrets", "{\"id\":\"safe\"}"));
        var properties = probe.Descriptor.Definitions["SecretValue"].GetProperty("properties");
        Assert.Equal("id", Assert.Single(properties.EnumerateObject()).Name);
        using var forged = JsonDocument.Parse("{\"id\":\"safe\",\"secret\":\"injected\"}");
        await Assert.ThrowsAsync<JsonException>(() => probe.Invoke("secrets", forged.RootElement).AsTask());
    }

    [Fact]
    public async Task DateTimeOffset_PreservesTicksAndOffsetsAtRootsNullableCollectionsAndStreamValues()
    {
        using var probe = CompileProbe(additionalSource: ConstructorSource);
        foreach (var text in new[]
        {
            "2026-10-06T12:34:56Z", "2026-10-06T12:34:56.1234567+05:45", "2026-10-06T12:34:56.0000001-03:30",
            "2026-10-06T00:00:00+14:00", "2026-10-06T00:00:00-14:00", "0001-01-01T00:00:00Z", "9999-12-31T23:59:59.9999999Z",
            "2026-10-06T12:34:56.123456700+05:45", "2026-10-06T12:34:56.000000000-00:00",
        })
        {
            var response = await probe.RoundTrip("timestamp", JsonSerializer.Serialize(text));
            var expected = DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            var actual = response.GetDateTimeOffset();
            Assert.Equal(expected.Ticks, actual.Ticks); Assert.Equal(expected.Offset, actual.Offset);
            Assert.DoesNotContain(".0000000", response.GetString(), StringComparison.Ordinal);
            AssertJson(response.GetRawText(), await probe.RoundTrip("nullable-timestamp", JsonSerializer.Serialize(text)));
            var list = "[null," + JsonSerializer.Serialize(text) + "]";
            AssertJson("[null," + response.GetRawText() + "]", await probe.RoundTrip("timestamps", list));
            var streamed = Assert.Single(await probe.Stream("timestamp-watch", list));
            AssertJson("[null," + response.GetRawText() + "]", streamed);
        }
        AssertJson("null", await probe.RoundTrip("nullable-timestamp", "null"));
        AssertJson("null", await probe.RoundTrip("timestamps", "null"));
        AssertJson("""{"when":"2026-10-06T12:34:56.1234567+05:45"}""",
            await probe.RoundTrip("timestamp-map", """{"when":"2026-10-06T12:34:56.1234567+05:45"}"""));
    }

    [Fact]
    public async Task DateTimeOffset_RejectsNonRepresentablePrecisionAndInvalidOffsetsWithoutDispatch()
    {
        using var probe = CompileProbe(additionalSource: ConstructorSource);
        foreach (var text in new[]
        {
            "2026-10-06T12:34:56.123456789+05:45", "2026-10-06T12:34:56.000000001Z", // valid profile, not exact CLR ticks
            "2026-10-06T12:34:56+14:01", "2026-10-06T12:34:56-15:00", "0001-01-01T00:00:00+00:01",
            "9999-12-31T23:59:59-00:01", "2026-10-06T12:34:60Z", "2026-10-06t12:34:56z", "2026-10-06T12:34:56",
        })
        {
            using var input = JsonDocument.Parse(JsonSerializer.Serialize(text));
            if (text.Contains("123456789", StringComparison.Ordinal) || text.Contains("000000001", StringComparison.Ordinal))
                Assert.True(probe.Descriptor.IsValid("#/$defs/CodecTimestampRequest", input.RootElement, out var error), error);
            await Assert.ThrowsAsync<JsonException>(() => probe.Invoke("timestamp", input.RootElement).AsTask());
            using var list = JsonDocument.Parse("[" + input.RootElement.GetRawText() + "]");
            await Assert.ThrowsAsync<JsonException>(() => probe.Invoke("timestamps", list.RootElement).AsTask());
        }
        Assert.Equal(0, probe.Calls());
    }

    [Fact]
    public async Task ConstructorValues_DepthFenceResetsAfterCyclicOutput()
    {
        using var probe = CompileProbe(additionalSource: ConstructorSource);
        using var unit = JsonDocument.Parse("{}");
        await Assert.ThrowsAsync<JsonException>(() => probe.Invoke("cyclic", unit.RootElement).AsTask());
        AssertJson("""{"id":"after-failure"}""", await probe.RoundTrip("optional-profile", """{"id":"after-failure"}"""));
    }

    private const string ConstructorSource = """
        public sealed partial class CodecService
        {
            public ValueTask<Profile> ProfileAsync(Profile request) => Respond(request with { });
            public async IAsyncEnumerable<Profile> ProfileWatchAsync(Profile request) { yield return await ProfileAsync(request); }
            public ValueTask<OptionalProfile> OptionalProfileAsync(OptionalProfile request) => Respond(request with { });
            public ValueTask<RecordValue> RecordStructAsync(RecordValue request) => Respond(request);
            public ValueTask<DateTimeOffset> TimestampAsync(DateTimeOffset value) => Respond(value);
            public ValueTask<DateTimeOffset?> NullableTimestampAsync(DateTimeOffset? value = null) => Respond(value);
            public ValueTask<IReadOnlyList<DateTimeOffset?>?> TimestampsAsync(IReadOnlyList<DateTimeOffset?>? values) => Respond(values);
            public ValueTask<IReadOnlyDictionary<string, DateTimeOffset>> TimestampMapAsync(IReadOnlyDictionary<string, DateTimeOffset> values) => Respond(values);
            public async IAsyncEnumerable<IReadOnlyList<DateTimeOffset?>?> TimestampWatchAsync(IReadOnlyList<DateTimeOffset?>? values) { yield return await TimestampsAsync(values); }
            public ValueTask<Cycle> CyclicAsync() { var value = new Cycle("cycle", default); value.Next = new(value); return Respond(value); }
        }
        public enum Scheduling { Sequential = 0, Parallel = 1 }
        public sealed record Profile(string Id, string? Description, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
            IReadOnlyList<Binding>? Bindings = null, bool IsInternal = true)
        {
            public Scheduling Mode { get; init; } = Scheduling.Sequential;
            [JsonPropertyName("link")] public string? URL { get; init; } = "default";
        }
        public sealed record Binding(string Name, IReadOnlyList<DateTimeOffset?>? Timestamps = null);
        public sealed record OptionalProfile([property: JsonPropertyName("id")] string ProfileId, SunderRpcOptional<string?> Note = default);
        public readonly record struct RecordValue(string Id, Scheduling Mode = Scheduling.Sequential);
        public sealed class Cycle
        {
            public Cycle(string id, SunderRpcOptional<Cycle> next) { Id = id; Next = next; }
            public string Id { get; }
            public SunderRpcOptional<Cycle> Next { get; set; }
        }
        """;
}
