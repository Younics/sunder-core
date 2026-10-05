using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Sunder.Sdk.Rpc;

#nullable enable

namespace Example.Authoring;

[SunderRpcService(SunderRpcExposure.Package)]
[SunderRpcDomainError(
    "value-invalid",
    Description = "The supplied value is invalid.",
    DetailsType = typeof(ErrorDetails))]
[SunderRpcDomainError(
    "conflict",
    Description = "The operation conflicts with current state.")]
public sealed partial class ParityService
{
    [SunderRpcMethod]
    public ValueTask<PingResult> PingAsync(
        SunderRpcInvocationContext context,
        CancellationToken cancellationToken) => throw new NotImplementedException();

    [SunderRpcMethod(
        Idempotency = SunderRpcMethodIdempotency.Idempotent,
        Errors = ["value-invalid", "conflict"])]
    public Task<Payload> RoundTripAsync(
        Payload request,
        SunderRpcInvocationContext context,
        CancellationToken cancellationToken) => throw new NotImplementedException();

    [SunderRpcMethod]
    public ValueTask<Result> CombineAsync(
        [SunderRpcFacet(MinLength = 1, MaxLength = 32)] string left,
        [SunderRpcFacet(Minimum = "0", Maximum = "100")] long right,
        SunderRpcInvocationContext context,
        CancellationToken cancellationToken) => throw new NotImplementedException();

    [SunderRpcMethod(Errors = ["value-invalid"])]
    public IAsyncEnumerable<Event> WatchAsync(
        WatchRequest request,
        SunderRpcInvocationContext context,
        CancellationToken cancellationToken) => throw new NotImplementedException();
}

[SunderRpcDefinition("BoundedText")]
[SunderRpcTransparent]
[SunderRpcFacet(
    MinLength = 1,
    MaxLength = 128,
    Title = "Bounded text",
    Description = "Text with explicit scalar-length bounds.")]
public sealed record BoundedText
{
    public required string Value { get; init; }
}

[SunderRpcDefinition("UnboundedText")]
[SunderRpcTransparent]
public sealed record UnboundedText
{
    public required string Value { get; init; }
}

[SunderRpcDefinition("Identifier")]
[SunderRpcTransparent]
public sealed record Identifier
{
    public required Guid Value { get; init; }
}

[SunderRpcDefinition("DateValue")]
[SunderRpcTransparent]
public sealed record DateValue
{
    public required DateOnly Value { get; init; }
}

[SunderRpcDefinition("Moment")]
[SunderRpcTransparent]
public sealed record Moment
{
    public required SunderRpcOffsetDateTime Value { get; init; }
}

[SunderRpcDefinition("TimeSpanValue")]
[SunderRpcTransparent]
public sealed record TimeSpanValue
{
    public required SunderRpcDuration Value { get; init; }
}

[SunderRpcDefinition("Link")]
[SunderRpcTransparent]
public sealed record Link
{
    public required SunderRpcUri Value { get; init; }
}

[SunderRpcDefinition("BigNumber")]
[SunderRpcTransparent]
[SunderRpcFacet(
    MaxDigits = 40,
    Minimum = "-9999999999999999999999999999999999999999",
    ExclusiveMaximum = "9999999999999999999999999999999999999999")]
public sealed record BigNumber
{
    public required BigInteger Value { get; init; }
}

[SunderRpcDefinition("DecimalNumber")]
[SunderRpcTransparent]
[SunderRpcFacet(
    MaxPrecision = 20,
    MaxScale = 8,
    ExclusiveMinimum = "-1000000000.125",
    Maximum = "1000000000.125")]
public sealed record DecimalNumber
{
    public required SunderRpcDecimal Value { get; init; }
}

[SunderRpcDefinition("Blob")]
[SunderRpcTransparent]
[SunderRpcFacet(MaxDecodedBytes = 4096)]
public sealed record Blob
{
    public required byte[] Value { get; init; }
}

[SunderRpcDefinition("UnboundedBytes")]
[SunderRpcTransparent]
public sealed record UnboundedBytes
{
    public required byte[] Value { get; init; }
}

[SunderRpcDefinition("Content")]
[SunderRpcTransparent]
public sealed record Content
{
    public required SunderRpcContentReference Value { get; init; }
}

[SunderRpcDefinition("AnyJson")]
[SunderRpcTransparent]
public sealed record AnyJson
{
    public required JsonElement Value { get; init; }
}

[SunderRpcDefinition("JsonObject")]
[SunderRpcTransparent]
[SunderRpcFacet(MaxProperties = 16)]
public sealed record JsonObject
{
    public required JsonElement Value { get; init; }
}

[SunderRpcDefinition("SafeCount")]
[SunderRpcTransparent]
[SunderRpcFacet(Minimum = "0", Maximum = "9007199254740991")]
public sealed record SafeCount
{
    public required long Value { get; init; }
}

[SunderRpcDefinition("Float32Value")]
[SunderRpcTransparent]
[SunderRpcFacet(ExclusiveMinimum = "-1000.25", Maximum = "1000.25")]
public sealed record Float32Value
{
    public required float Value { get; init; }
}

[SunderRpcDefinition("Float64Value")]
[SunderRpcTransparent]
[SunderRpcFacet(Minimum = "-100000000000000000000", ExclusiveMaximum = "100000000000000000000")]
public sealed record Float64Value
{
    public required double Value { get; init; }
}

[SunderRpcDefinition("ExactRatio")]
[SunderRpcTransparent]
[SunderRpcFacet(ExactNumber = "0.5")]
public sealed record ExactRatio
{
    public required double Value { get; init; }
}

[SunderRpcDefinition("ScalarId")]
[SunderRpcTransparent]
[SunderRpcFacet(MinLength = 1, MaxLength = 64)]
public sealed record ScalarId
{
    public required string Value { get; init; }
}

[SunderRpcDefinition("StringList")]
[SunderRpcTransparent]
[SunderRpcFacet(
    MinItems = 0,
    MaxItems = 32,
    Title = "String list",
    Description = "A bounded transparent array.")]
public sealed record StringList
{
    public required IReadOnlyList<BoundedText> Value { get; init; }
}

[SunderRpcDefinition("UnboundedList")]
[SunderRpcTransparent]
public sealed record UnboundedList
{
    public required IReadOnlyList<bool> Value { get; init; }
}

[SunderRpcDefinition("IntegerMap")]
[SunderRpcTransparent]
[SunderRpcFacet(
    MinLength = 1,
    MaxLength = 64,
    MinProperties = 0,
    MaxProperties = 16,
    Title = "Integer map",
    Description = "A bounded transparent string-keyed map.")]
public sealed record IntegerMap
{
    public required IReadOnlyDictionary<string, long> Value { get; init; }
}

[SunderRpcDefinition("UnboundedMap")]
[SunderRpcTransparent]
public sealed record UnboundedMap
{
    public required IReadOnlyDictionary<string, string> Value { get; init; }
}

[SunderRpcDefinition("StringStatus")]
[SunderRpcFacet(Title = "String status", Description = "Declaration order is wire-significant.")]
public enum StringStatus
{
    [JsonStringEnumMemberName("zeta")]
    Zeta,

    [JsonStringEnumMemberName("alpha")]
    Alpha,

    [JsonStringEnumMemberName("middle")]
    Middle,
}

[SunderRpcDefinition("NumericPriority")]
[SunderRpcNumericEnum]
public enum NumericPriority : long
{
    Ten = 10,
    MinusOne = -1,
    Two = 2,
}

[SunderRpcDefinition("Node")]
public sealed record Node
{
    public required BoundedText Value { get; init; }
    public SunderRpcOptional<Node> Next { get; init; }
    public required IReadOnlyList<Node> Children { get; init; }
}

[SunderRpcDefinition("TextChoice")]
public sealed record TextChoice : Choice
{
    [SunderRpcFacet(ConstantString = "text")]
    public required string Kind { get; init; }

    public required BoundedText Text { get; init; }
}

[SunderRpcDefinition("CountChoice")]
public sealed record CountChoice : Choice
{
    [SunderRpcFacet(ConstantString = "count")]
    public required string Kind { get; init; }

    public required SafeCount Count { get; init; }
}

[SunderRpcDefinition("Choice")]
[SunderRpcUnion(typeof(TextChoice), typeof(CountChoice))]
[SunderRpcFacet(Title = "Choice", Description = "A source-level untagged union.")]
public abstract record Choice;

[SunderRpcDefinition("ProgressEvent")]
[SunderRpcVariant("progress")]
public sealed record ProgressEvent : Event
{
    public required SafeCount Sequence { get; init; }
    public required ExactRatio Ratio { get; init; }
}

[SunderRpcDefinition("FinishedEvent")]
[SunderRpcVariant("finished")]
public sealed record FinishedEvent : Event
{
    public required Result Result { get; init; }
}

[SunderRpcDefinition("Event")]
[SunderRpcUnion(typeof(ProgressEvent), typeof(FinishedEvent))]
[SunderRpcDiscriminator("kind")]
[SunderRpcFacet(Title = "Parity event", Description = "A tagged stream event.")]
public abstract record Event;

[SunderRpcDefinition("ErrorDetails")]
public sealed record ErrorDetails
{
    public required BoundedText Field { get; init; }
    public required BoundedText Reason { get; init; }
}

[SunderRpcDefinition("PingResult")]
public sealed record PingResult
{
    public required bool Ok { get; init; }
}

[SunderRpcDefinition("Result")]
public sealed record Result
{
    public required bool Accepted { get; init; }
    public SunderRpcOptional<string?> Note { get; init; }
}

[SunderRpcDefinition("WatchRequest")]
public sealed record WatchRequest
{
    public SunderRpcOptional<BoundedText> Cursor { get; init; }
}

[SunderRpcDefinition("Payload")]
[SunderRpcFacet(MinProperties = 29, MaxProperties = 31, Title = "Parity payload", Description = "Exercises the complete V1 value profile.")]
public sealed record Payload
{
    [SunderRpcFacet(MinLength = 1, MaxLength = 128)]
    public required string RequiredText { get; init; }

    [SunderRpcFacet(MinLength = 1, MaxLength = 128)]
    public SunderRpcOptional<string> OptionalText { get; init; }

    [SunderRpcFacet(MinLength = 1, MaxLength = 128)]
    public required string? RequiredNullableText { get; init; }

    [SunderRpcFacet(MinLength = 1, MaxLength = 128)]
    public SunderRpcOptional<string?> OptionalNullableText { get; init; }
    public required UnboundedText UnboundedText { get; init; }
    public required bool Flag { get; init; }
    public required SafeCount Count { get; init; }
    public required Float32Value Float32 { get; init; }
    public required Float64Value Float64 { get; init; }
    public required ExactRatio ExactRatio { get; init; }
    public required Identifier Id { get; init; }
    public required DateValue Date { get; init; }
    public required Moment Timestamp { get; init; }
    public required TimeSpanValue Duration { get; init; }
    public required Link Uri { get; init; }
    public required BigNumber BigInteger { get; init; }
    public required DecimalNumber Decimal { get; init; }
    public required Blob Bytes { get; init; }
    public required UnboundedBytes UnboundedBytes { get; init; }
    public required Content Content { get; init; }
    public required AnyJson Json { get; init; }
    public required JsonObject JsonObject { get; init; }
    public required StringList Tags { get; init; }
    public required UnboundedList UnboundedList { get; init; }
    public required IntegerMap Scores { get; init; }
    public required UnboundedMap UnboundedMap { get; init; }
    public required StringStatus Status { get; init; }
    public required NumericPriority Priority { get; init; }
    public required ScalarId ScalarId { get; init; }
    public required Node Node { get; init; }
    public required Event Event { get; init; }
    public required Choice Choice { get; init; }
}
