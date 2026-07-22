using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Stacks;

/// <summary>Defines standard typed extension points used by Stack hosts.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StackContributionsV1)]
public static class SunderStackExtensionPoints
{
    /// <summary>Gets the extension point for activation-scoped Stack exporters.</summary>
    public static readonly PackageExtensionPoint<IPackageStackExporter> StackExporters =
        new("sunder:stack-exporters");

    /// <summary>Gets the extension point for activation-scoped Stack importers.</summary>
    public static readonly PackageExtensionPoint<IPackageStackImporter> StackImporters =
        new("sunder:stack-importers");

    /// <summary>Gets the Runtime extension point for post-import synchronization handlers.</summary>
    public static readonly PackageExtensionPoint<IPackageStackImportAppliedHandler> StackImportAppliedHandlers =
        new("sunder:stack-import-applied-handlers");
}
