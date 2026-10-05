using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sunder.Sdk.Rpc;

#nullable enable

namespace Example.Authoring.Extensions;

[SunderRpcService(SunderRpcExposure.Public)]
public interface AuditService
{
    [SunderRpcMethod]
    IAsyncEnumerable<AuditEvent> ObserveAsync(
        AuditRequest request,
        SunderRpcInvocationContext context,
        CancellationToken cancellationToken);
}

[SunderRpcService(SunderRpcExposure.Public)]
public interface ClockService
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<ClockResult> NowAsync(
        SunderRpcInvocationContext context,
        CancellationToken cancellationToken);
}

[SunderRpcProvider(
    typeof(ClockService),
    ProviderId = "example.authoring.extensions.clock.primary")]
public sealed partial class PrimaryClockProvider : ClockService
{
    public ValueTask<ClockResult> NowAsync(
        SunderRpcInvocationContext context,
        CancellationToken cancellationToken) => throw new NotImplementedException();
}

[SunderRpcProvider(
    typeof(ClockService),
    ProviderId = "example.authoring.extensions.clock.owner",
    Audience = SunderRpcProviderAudience.Owner)]
public sealed partial class OwnerClockProvider : ClockService
{
    public ValueTask<ClockResult> NowAsync(
        SunderRpcInvocationContext context,
        CancellationToken cancellationToken) => throw new NotImplementedException();
}

[SunderRpcDefinition("AuditRequest")]
public sealed record AuditRequest
{
    public SunderRpcOptional<string> Cursor { get; init; }
}

[SunderRpcDefinition("AuditEvent")]
public sealed record AuditEvent
{
    public required long Sequence { get; init; }
}

[SunderRpcDefinition("ClockResult")]
public sealed record ClockResult
{
    public required SunderRpcOffsetDateTime Timestamp { get; init; }
}
