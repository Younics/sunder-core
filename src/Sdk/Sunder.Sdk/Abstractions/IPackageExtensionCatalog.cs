using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.ExtensionsV1)]
public interface IPackageExtensionCatalog
{
    IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint);

    IReadOnlyList<PackageExtensionContribution<TContract>> GetExtensionContributions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
        => GetExtensions(extensionPoint)
            .Select(contribution => new PackageExtensionContribution<TContract>(string.Empty, contribution))
            .ToArray();
}

[SunderSdkCapability(SunderSdkCapabilities.ExtensionsV1)]
public sealed record PackageExtensionContribution<TContract>(
    string PackageId,
    TContract Contribution);
