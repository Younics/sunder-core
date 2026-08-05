using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Defines host-managed background work scoped to one package activation.</summary>
/// <remarks>The host calls start once after activation and stop once before disposal. Implementations must be thread-safe and stop promptly when cancellation is requested.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.BackgroundServicesV1)]
public interface IPackageBackgroundService
{
    /// <summary>Starts background work; completion means startup finished, not that long-running work ended.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests orderly shutdown and completes after owned work and resources have stopped.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
