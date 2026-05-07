namespace Sunder.Sdk.Abstractions;

public interface IPackageExtensionCatalogChangeNotifier
{
    event Action? ExtensionsChanged;
}
