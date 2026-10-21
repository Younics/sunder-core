namespace Sunder.Runtime.Host.Services;

/// <summary>Host-only join from the selected provider's actual worker to its reserved execution.</summary>
internal sealed record RuntimeExecutionReservationIdentity(
    string LeaseId,
    string PackageId,
    string GenerationId,
    Guid ActivationId);

internal sealed partial class ProcessRuntimeWorker
{
    // These are immutable construction-time identities. The registry supplies all OS and
    // containment identity from its durable lease; no package-provided PID is accepted.
    internal RuntimeExecutionReservationIdentity ExecutionReservationIdentity
        => new(_launch.LeaseId, _package.PackageId, _package.SessionId, _activationId);
}
