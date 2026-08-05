using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Optionally prepares an App-owned package view before its first presentation.</summary>
/// <remarks>Warmup is parameter-free, may run while the view is hidden, and must not depend on visual attachment. Implementations should be idempotent, honor cancellation promptly, and avoid durable mutations or shell navigation. The host invokes normal navigation separately when the view is presented.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.ShellViewV1)]
public interface IPackageViewWarmupTarget
{
    /// <summary>Prepares reusable view data without presenting the view.</summary>
    ValueTask WarmupAsync(CancellationToken cancellationToken = default);
}
