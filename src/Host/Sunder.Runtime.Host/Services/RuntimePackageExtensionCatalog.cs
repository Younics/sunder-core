using Sunder.Sdk.Abstractions;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageExtensionCatalog : IPackageExtensionCatalog, IPackageExtensionCatalogChangeNotifier
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, List<RuntimePackageExtensionContribution>> _extensions = new(StringComparer.OrdinalIgnoreCase);

    public event Action? ExtensionsChanged;

    public void Add<TContract>(string packageId, PackageExtensionPoint<TContract> extensionPoint, TContract contribution)
    {
        lock (_syncRoot)
        {
            if (!_extensions.TryGetValue(extensionPoint.Id, out var contributions))
            {
                contributions = [];
                _extensions[extensionPoint.Id] = contributions;
            }

            contributions.Add(new RuntimePackageExtensionContribution(packageId, contribution!));
        }

        ExtensionsChanged?.Invoke();
    }

    public void RemovePackage(string packageId)
    {
        var removed = false;
        lock (_syncRoot)
        {
            foreach (var extensionId in _extensions.Keys.ToArray())
            {
                var contributions = _extensions[extensionId];
                removed |= contributions.RemoveAll(contribution => string.Equals(contribution.PackageId, packageId, StringComparison.OrdinalIgnoreCase)) > 0;
                if (contributions.Count == 0)
                {
                    _extensions.Remove(extensionId);
                }
            }
        }

        if (removed)
        {
            ExtensionsChanged?.Invoke();
        }
    }

    public IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
    {
        lock (_syncRoot)
        {
            return _extensions.TryGetValue(extensionPoint.Id, out var contributions)
                ? contributions.Select(contribution => contribution.Instance).OfType<TContract>().ToArray()
                : [];
        }
    }

    private sealed record RuntimePackageExtensionContribution(string PackageId, object Instance);
}
