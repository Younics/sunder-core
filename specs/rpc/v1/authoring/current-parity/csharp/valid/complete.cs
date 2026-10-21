using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

#nullable enable

namespace Example.Authoring;

[SunderService(SunderServiceExposure.Package)]
[SunderServiceError("value-invalid", Description = "The supplied value is invalid.", DetailsType = typeof(ErrorDetails))]
[SunderServiceError("conflict", Description = "The operation conflicts with current state.")]
public sealed partial class ParityService
{
    public ValueTask<PingResult> PingAsync(SunderRpcInvocationContext context, CancellationToken cancellationToken) => throw new NotImplementedException();

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["value-invalid", "conflict"])]
    public Task<Payload> RoundTripAsync(Payload request, SunderRpcInvocationContext context, CancellationToken cancellationToken) => throw new NotImplementedException();

    public ValueTask<Result> CombineAsync(
        [SunderValueConstraints(MinLength = 1, MaxLength = 32)] string left,
        [SunderValueConstraints(Minimum = "0", Maximum = "100")] long right,
        SunderRpcInvocationContext context, CancellationToken cancellationToken) => throw new NotImplementedException();

    [SunderMethod(Errors = ["value-invalid"])]
    public IAsyncEnumerable<Event> WatchAsync(WatchRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken) => throw new NotImplementedException();
}

[SunderValue("BoundedText"), SunderTransparentValue]
[SunderValueConstraints(MinLength = 1, MaxLength = 128, Title = "Bounded text", Description = "Text with explicit scalar-length bounds.")]
public sealed record BoundedText { public required string Value { get; init; } }
[SunderValue("UnboundedText"), SunderTransparentValue]
public sealed record UnboundedText { public required string Value { get; init; } }
[SunderValue("Identifier"), SunderTransparentValue]
public sealed record Identifier { public required Guid Value { get; init; } }
[SunderValue("DateValue"), SunderTransparentValue]
public sealed record DateValue { public required DateOnly Value { get; init; } }
[SunderValue("Moment"), SunderTransparentValue]
public sealed record Moment { public required SunderRpcOffsetDateTime Value { get; init; } }
[SunderValue("TimeSpanValue"), SunderTransparentValue]
public sealed record TimeSpanValue { public required SunderRpcDuration Value { get; init; } }
[SunderValue("Link"), SunderTransparentValue]
public sealed record Link { public required SunderRpcUri Value { get; init; } }
[SunderValue("BigNumber"), SunderTransparentValue]
[SunderValueConstraints(MaxDigits = 40, Minimum = "-9999999999999999999999999999999999999999", ExclusiveMaximum = "9999999999999999999999999999999999999999")]
public sealed record BigNumber { public required BigInteger Value { get; init; } }
[SunderValue("DecimalNumber"), SunderTransparentValue]
[SunderValueConstraints(MaxPrecision = 20, MaxScale = 8, ExclusiveMinimum = "-1000000000.125", Maximum = "1000000000.125")]
public sealed record DecimalNumber { public required SunderRpcDecimal Value { get; init; } }
[SunderValue("Blob"), SunderTransparentValue, SunderValueConstraints(MaxDecodedBytes = 4096)]
public sealed record Blob { public required byte[] Value { get; init; } }
[SunderValue("UnboundedBytes"), SunderTransparentValue]
public sealed record UnboundedBytes { public required byte[] Value { get; init; } }
[SunderValue("Content"), SunderTransparentValue]
public sealed record Content { public required SunderRpcContentReference Value { get; init; } }
[SunderValue("AnyJson"), SunderTransparentValue]
public sealed record AnyJson { public required JsonElement Value { get; init; } }
[SunderValue("JsonObject"), SunderTransparentValue, SunderValueConstraints(MaxProperties = 16)]
public sealed record JsonObject { public required JsonElement Value { get; init; } }
[SunderValue("SafeCount"), SunderTransparentValue, SunderValueConstraints(Minimum = "0", Maximum = "9007199254740991")]
public sealed record SafeCount { public required long Value { get; init; } }
[SunderValue("Float32Value"), SunderTransparentValue, SunderValueConstraints(ExclusiveMinimum = "-1000.25", Maximum = "1000.25")]
public sealed record Float32Value { public required float Value { get; init; } }
[SunderValue("Float64Value"), SunderTransparentValue, SunderValueConstraints(Minimum = "-100000000000000000000", ExclusiveMaximum = "100000000000000000000")]
public sealed record Float64Value { public required double Value { get; init; } }
[SunderValue("ExactRatio"), SunderTransparentValue, SunderValueConstraints(ExactNumber = "0.5")]
public sealed record ExactRatio { public required double Value { get; init; } }
[SunderValue("ScalarId"), SunderTransparentValue, SunderValueConstraints(MinLength = 1, MaxLength = 64)]
public sealed record ScalarId { public required string Value { get; init; } }
[SunderValue("StringList"), SunderTransparentValue]
[SunderValueConstraints(MinItems = 0, MaxItems = 32, Title = "String list", Description = "A bounded transparent array.")]
public sealed record StringList { public required IReadOnlyList<BoundedText> Value { get; init; } }
[SunderValue("UnboundedList"), SunderTransparentValue]
public sealed record UnboundedList { public required IReadOnlyList<bool> Value { get; init; } }
[SunderValue("IntegerMap"), SunderTransparentValue]
[SunderValueConstraints(MinLength = 1, MaxLength = 64, MinProperties = 0, MaxProperties = 16, Title = "Integer map", Description = "A bounded transparent string-keyed map.")]
public sealed record IntegerMap { public required IReadOnlyDictionary<string, long> Value { get; init; } }
[SunderValue("UnboundedMap"), SunderTransparentValue]
public sealed record UnboundedMap { public required IReadOnlyDictionary<string, string> Value { get; init; } }

[SunderValue("StringStatus"), SunderValueConstraints(Title = "String status", Description = "Declaration order is wire-significant.")]
public enum StringStatus
{
    [JsonStringEnumMemberName("zeta")] Zeta,
    [JsonStringEnumMemberName("alpha")] Alpha,
    [JsonStringEnumMemberName("middle")] Middle,
}
[SunderValue("NumericPriority"), SunderNumericEnum]
public enum NumericPriority : long { Ten = 10, MinusOne = -1, Two = 2 }
[SunderValue("Node")]
public sealed record Node
{
    public required BoundedText Value { get; init; }
    public SunderRpcOptional<Node> Next { get; init; }
    public required IReadOnlyList<Node> Children { get; init; }
}
[SunderValue("TextChoice")]
public sealed record TextChoice : Choice
{
    [SunderValueConstraints(ConstantString = "text")] public required string Kind { get; init; }
    public required BoundedText Text { get; init; }
}
[SunderValue("CountChoice")]
public sealed record CountChoice : Choice
{
    [SunderValueConstraints(ConstantString = "count")] public required string Kind { get; init; }
    public required SafeCount Count { get; init; }
}
[SunderValue("Choice"), SunderUnion(typeof(TextChoice), typeof(CountChoice))]
[SunderValueConstraints(Title = "Choice", Description = "A source-level untagged union.")]
public abstract record Choice;
[SunderValue("ProgressEvent"), SunderVariant("progress")]
public sealed record ProgressEvent : Event
{
    public required SafeCount Sequence { get; init; }
    public required ExactRatio Ratio { get; init; }
}
[SunderValue("FinishedEvent"), SunderVariant("finished")]
public sealed record FinishedEvent : Event { public required Result Result { get; init; } }
[SunderValue("Event"), SunderUnion(typeof(ProgressEvent), typeof(FinishedEvent)), SunderDiscriminator("kind")]
[SunderValueConstraints(Title = "Parity event", Description = "A tagged stream event.")]
public abstract record Event;
[SunderValue("ErrorDetails")]
public sealed record ErrorDetails
{
    public required BoundedText Field { get; init; }
    public required BoundedText Reason { get; init; }
}
[SunderValue("PingResult")]
public sealed record PingResult { public required bool Ok { get; init; } }
[SunderValue("Result")]
public sealed record Result
{
    public required bool Accepted { get; init; }
    public SunderRpcOptional<string?> Note { get; init; }
}
[SunderValue("WatchRequest")]
public sealed record WatchRequest { public SunderRpcOptional<BoundedText> Cursor { get; init; } }
[SunderValue("Payload"), SunderValueConstraints(MinProperties = 29, MaxProperties = 31, Title = "Parity payload", Description = "Exercises the complete V1 value profile.")]
public sealed record Payload
{
    [SunderValueConstraints(MinLength = 1, MaxLength = 128)] public required string RequiredText { get; init; }
    [SunderValueConstraints(MinLength = 1, MaxLength = 128)] public SunderRpcOptional<string> OptionalText { get; init; }
    [SunderValueConstraints(MinLength = 1, MaxLength = 128)] public required string? RequiredNullableText { get; init; }
    [SunderValueConstraints(MinLength = 1, MaxLength = 128)] public SunderRpcOptional<string?> OptionalNullableText { get; init; }
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
