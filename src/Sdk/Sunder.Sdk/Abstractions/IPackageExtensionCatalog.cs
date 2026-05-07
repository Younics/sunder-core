namespace Sunder.Sdk.Abstractions;

public interface IPackageExtensionCatalog
{
    IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint);
}
