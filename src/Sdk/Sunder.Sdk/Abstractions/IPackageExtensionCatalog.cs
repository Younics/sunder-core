using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides thread-safe snapshots of contributions from currently active packages.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ExtensionsV1)]
public interface IPackageExtensionCatalog
{
    /// <summary>Gets contribution instances in deterministic host order; the host retains their activation ownership.</summary>
    IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint);

    /// <summary>Gets contribution instances together with their owning package ids.</summary>
    IReadOnlyList<PackageExtensionContribution<TContract>> GetExtensionContributions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
        => GetExtensions(extensionPoint)
            .Select(contribution => new PackageExtensionContribution<TContract>(string.Empty, contribution))
            .ToArray();
}

/// <summary>Associates an active contribution instance with its owning package.</summary>
/// <param name="PackageId">Owner id, or an empty string when a legacy catalog cannot supply ownership.</param>
/// <param name="Contribution">Host-owned activation-scoped contribution instance.</param>
[SunderSdkCapability(SunderSdkCapabilities.ExtensionsV1)]
public sealed record PackageExtensionContribution<TContract>(
    string PackageId,
    TContract Contribution);
