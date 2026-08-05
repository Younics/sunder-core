using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Identifies one committed Runtime package activation generation.</summary>
/// <param name="ActivationId">The host-assigned identity for the exact package activation.</param>
/// <param name="SessionGeneration">The committed Runtime session generation containing the activation.</param>
[SunderSdkCapability(SunderSdkCapabilities.RuntimeGenerationsV1)]
public sealed record PackageRuntimeGeneration(Guid ActivationId, long SessionGeneration);

/// <summary>
/// Extends a background service with work that must begin only after its package activation is committed.
/// </summary>
/// <remarks>
/// <see cref="IPackageBackgroundService.StartAsync"/> runs while a replacement session is still a candidate and
/// must remain non-destructive. The host calls <see cref="CommitGenerationAsync"/> only for the published
/// activation. A candidate that is discarded receives <see cref="IPackageBackgroundService.StopAsync"/> without
/// a generation commit.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.RuntimeGenerationsV1)]
public interface IPackageRuntimeGenerationParticipant : IPackageBackgroundService
{
    /// <summary>Completes when the committed generation stops and faults when it can no longer operate safely.</summary>
    /// <remarks>The host observes failures and deactivates the exact package generation that reported them.</remarks>
    Task GenerationCompletion => Task.CompletedTask;

    /// <summary>Activates generation-owned recovery and dispatch after Runtime publication.</summary>
    /// <remarks>The host can retry the exact same generation during publication reconciliation; implementations must be idempotent.</remarks>
    Task CommitGenerationAsync(
        PackageRuntimeGeneration generation,
        CancellationToken cancellationToken = default);
}
