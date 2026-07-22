using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Describes one atomic revision of the active extension catalog.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ExtensionChangesV1)]
public sealed class PackageExtensionCatalogChangedEventArgs(
    long revision,
    PackageExtensionCatalogChangeReason reason,
    IReadOnlyList<PackageExtensionChange> changes) : EventArgs
{
    /// <summary>Gets the monotonically increasing catalog revision.</summary>
    public long Revision { get; } = revision;

    /// <summary>Gets the package lifecycle operation that caused the revision.</summary>
    public PackageExtensionCatalogChangeReason Reason { get; } = reason;

    /// <summary>Gets the immutable ordered changes in this revision.</summary>
    public IReadOnlyList<PackageExtensionChange> Changes { get; } = changes;

    /// <summary>Determines whether the revision affects the given case-insensitive extension-point id.</summary>
    public bool IncludesExtensionPoint(string extensionPointId)
        => Changes.Any(change => string.Equals(change.ExtensionPointId, extensionPointId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Identifies the package lifecycle event that changed the catalog.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ExtensionChangesV1)]
public enum PackageExtensionCatalogChangeReason
{
    /// <summary>A package completed activation.</summary>
    PackageActivated = 0,
    /// <summary>An active package was deactivated.</summary>
    PackageDeactivated = 1,
    /// <summary>A package fault removed contributions.</summary>
    PackageFaulted = 2,
    /// <summary>A package was removed from local installed state.</summary>
    PackageUninstalled = 3,
    /// <summary>An installed package was disabled.</summary>
    PackageDisabled = 4,
}

/// <summary>Describes how an extension contribution changed.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ExtensionChangesV1)]
public enum PackageExtensionChangeKind
{
    /// <summary>A contribution became available.</summary>
    Added = 0,
    /// <summary>A contribution became unavailable.</summary>
    Removed = 1,
}

/// <summary>Describes one contribution-level catalog mutation.</summary>
/// <param name="PackageId">Package that owns the contribution.</param>
/// <param name="ExtensionPointId">Stable affected extension-point id.</param>
/// <param name="Kind">Mutation kind.</param>
/// <param name="ContributionType">Runtime contribution type.</param>
[SunderSdkCapability(SunderSdkCapabilities.ExtensionChangesV1)]
public sealed record PackageExtensionChange(
    string PackageId,
    string ExtensionPointId,
    PackageExtensionChangeKind Kind,
    Type ContributionType);
