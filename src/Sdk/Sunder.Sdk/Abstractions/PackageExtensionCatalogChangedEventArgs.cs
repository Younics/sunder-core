using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.ExtensionChangesV1)]
public sealed class PackageExtensionCatalogChangedEventArgs(
    long revision,
    PackageExtensionCatalogChangeReason reason,
    IReadOnlyList<PackageExtensionChange> changes) : EventArgs
{
    public long Revision { get; } = revision;

    public PackageExtensionCatalogChangeReason Reason { get; } = reason;

    public IReadOnlyList<PackageExtensionChange> Changes { get; } = changes;

    public bool IncludesExtensionPoint(string extensionPointId)
        => Changes.Any(change => string.Equals(change.ExtensionPointId, extensionPointId, StringComparison.OrdinalIgnoreCase));
}

[SunderSdkCapability(SunderSdkCapabilities.ExtensionChangesV1)]
public enum PackageExtensionCatalogChangeReason
{
    PackageActivated = 0,
    PackageDeactivated = 1,
    PackageReloaded = 2,
    PackageFaulted = 3,
    PackageInstalled = 4,
    PackageUninstalled = 5,
    PackageEnabled = 6,
    PackageDisabled = 7,
    PackageUpdated = 8,
}

[SunderSdkCapability(SunderSdkCapabilities.ExtensionChangesV1)]
public enum PackageExtensionChangeKind
{
    Added = 0,
    Removed = 1,
    Replaced = 2,
}

[SunderSdkCapability(SunderSdkCapabilities.ExtensionChangesV1)]
public sealed record PackageExtensionChange(
    string PackageId,
    string ExtensionPointId,
    PackageExtensionChangeKind Kind,
    Type? ContributionType = null,
    Version? PackageVersion = null);
