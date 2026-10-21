using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Example.Authoring.Extensions;

[SunderService(SunderServiceExposure.Public)]
public interface AuditService
{
    IAsyncEnumerable<AuditEvent> ObserveAsync(AuditRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken);
}

[SunderService(SunderServiceExposure.Public)]
public interface ClockService
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<ClockResult> NowAsync(SunderRpcInvocationContext context, CancellationToken cancellationToken);
}

[SunderServiceProvider(typeof(ClockService), ProviderId = "example.authoring.extensions.clock.primary")]
public sealed partial class PrimaryClockProvider : ClockService
{
    public ValueTask<ClockResult> NowAsync(SunderRpcInvocationContext context, CancellationToken cancellationToken) => throw new NotImplementedException();
}

[SunderServiceProvider(typeof(ClockService), ProviderId = "example.authoring.extensions.clock.owner", Audience = SunderServiceAudience.Owner)]
public sealed partial class OwnerClockProvider : ClockService
{
    public ValueTask<ClockResult> NowAsync(SunderRpcInvocationContext context, CancellationToken cancellationToken) => throw new NotImplementedException();
}

[SunderValue("AuditRequest")]
public sealed record AuditRequest
{
    public SunderRpcOptional<string> Cursor { get; init; }
}
[SunderValue("AuditEvent")]
public sealed record AuditEvent
{
    public required long Sequence { get; init; }
}
[SunderValue("ClockResult")]
public sealed record ClockResult
{
    public required SunderRpcOffsetDateTime Timestamp { get; init; }
}
