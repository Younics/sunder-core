using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Worker;

/// <summary>Defines one package-owned service that participates in the staged Worker V1 lifecycle.</summary>
/// <remarks>
/// The worker prepares services in registration order before generation commit, awaits activation before
/// acknowledging publication readiness, observes <see cref="Completion"/> while active, and shuts prepared
/// services down in reverse order. <see cref="ShutdownAsync"/> can run after candidate preparation even when the
/// candidate was never committed or activated.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.BackgroundServicesV1)]
[SunderSdkCapability(SunderSdkCapabilities.RuntimeGenerationsV1)]
[SunderSdkCapability(SunderWorkerCapabilities.ProtocolV1)]
public interface ISunderWorkerLifecycleService
{
    /// <summary>Performs non-destructive candidate preparation before any generation is committed.</summary>
    ValueTask PrepareCandidateAsync(CancellationToken cancellationToken = default);

    /// <summary>Commits local, idempotent state for the exact Host-assigned generation.</summary>
    ValueTask CommitGenerationAsync(
        SunderWorkerGenerationContext generation,
        CancellationToken cancellationToken = default);

    /// <summary>Starts generation-owned work that must be ready before the worker can be published.</summary>
    ValueTask ActivateAsync(
        SunderWorkerContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the stable task monitored for an unexpected active-service failure after activation.</summary>
    /// <remarks>A fault or cancellation outside shutdown faults the worker activation.</remarks>
    Task Completion { get; }

    /// <summary>Stops owned work and releases resources for a prepared candidate or active generation.</summary>
    ValueTask ShutdownAsync(
        SunderWorkerShutdownContext context,
        CancellationToken cancellationToken = default);
}
