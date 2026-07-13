using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides thread-safe snapshots of contributions from currently active packages.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ExtensionsV1)]
public interface IPackageExtensionCatalog
{
    /// <summary>Gets contribution instances in deterministic host order; the host retains their activation ownership.</summary>
    IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint);

    /// <summary>Gets contribution instances together with their mandatory owning package ids.</summary>
    IReadOnlyList<PackageExtensionContribution<TContract>> GetExtensionContributions<TContract>(PackageExtensionPoint<TContract> extensionPoint);
}

/// <summary>Associates an active contribution instance with its owning package.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ExtensionsV1)]
public sealed record PackageExtensionContribution<TContract>
{
    /// <summary>Creates an owned extension contribution.</summary>
    /// <param name="packageId">Canonical id of the package that owns the activation.</param>
    /// <param name="contribution">Host-owned activation-scoped contribution instance.</param>
    public PackageExtensionContribution(string packageId, TContract contribution)
    {
        if (!Sunder.Sdk.Packaging.PackageId.TryParse(packageId, out _))
        {
            throw new ArgumentException("Extension contribution ownership requires a canonical package id.", nameof(packageId));
        }

        ArgumentNullException.ThrowIfNull(contribution);
        PackageId = packageId;
        Contribution = contribution;
    }

    /// <summary>Gets the canonical id of the package that owns the activation.</summary>
    public string PackageId { get; }

    /// <summary>Gets the host-owned activation-scoped contribution instance.</summary>
    public TContract Contribution { get; }

    /// <summary>Deconstructs the contribution into its owner id and instance.</summary>
    public void Deconstruct(out string packageId, out TContract contribution)
    {
        packageId = PackageId;
        contribution = Contribution;
    }
}
