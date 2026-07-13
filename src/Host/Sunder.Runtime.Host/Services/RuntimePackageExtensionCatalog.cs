using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageExtensionCatalog : IPackageExtensionCatalog, IPackageExtensionCatalogChangeNotifier, IPackageExtensionCatalogMonitor
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, List<RuntimePackageExtensionContribution>> _extensions = new(StringComparer.OrdinalIgnoreCase);
    private long _revision;

    public event EventHandler? ExtensionsChanged;

    public event EventHandler<PackageExtensionCatalogChangedEventArgs>? Changed;

    public void Add<TContract>(string packageId, PackageExtensionPoint<TContract> extensionPoint, TContract contribution)
    {
        if (!PackageId.TryParse(packageId, out _))
        {
            throw new ArgumentException("Extension contribution ownership requires a canonical package id.", nameof(packageId));
        }
        ArgumentNullException.ThrowIfNull(extensionPoint);
        ArgumentNullException.ThrowIfNull(contribution);

        lock (_syncRoot)
        {
            if (!_extensions.TryGetValue(extensionPoint.Id, out var contributions))
            {
                contributions = [];
                _extensions[extensionPoint.Id] = contributions;
            }

            contributions.Add(new RuntimePackageExtensionContribution(packageId, contribution));
        }

        RaiseChanged(
            PackageExtensionCatalogChangeReason.PackageActivated,
            [new PackageExtensionChange(packageId, extensionPoint.Id, PackageExtensionChangeKind.Added, contribution?.GetType())]);
    }

    public void RemovePackage(
        string packageId,
        PackageExtensionCatalogChangeReason reason = PackageExtensionCatalogChangeReason.PackageDeactivated)
    {
        var changes = new List<PackageExtensionChange>();
        lock (_syncRoot)
        {
            foreach (var extensionId in _extensions.Keys.ToArray())
            {
                var contributions = _extensions[extensionId];
                var removedContributions = contributions
                    .Where(contribution => string.Equals(contribution.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                foreach (var removedContribution in removedContributions)
                {
                    changes.Add(new PackageExtensionChange(
                        removedContribution.PackageId,
                        extensionId,
                        PackageExtensionChangeKind.Removed,
                        removedContribution.Instance.GetType()));
                }

                if (removedContributions.Length > 0)
                {
                    contributions.RemoveAll(contribution => string.Equals(contribution.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
                }

                if (contributions.Count == 0)
                {
                    _extensions.Remove(extensionId);
                }
            }
        }

        RaiseChanged(reason, changes);
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

    public IReadOnlyList<PackageExtensionContribution<TContract>> GetExtensionContributions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
    {
        lock (_syncRoot)
        {
            return _extensions.TryGetValue(extensionPoint.Id, out var contributions)
                ? contributions
                    .Where(contribution => contribution.Instance is TContract)
                    .Select(contribution => new PackageExtensionContribution<TContract>(contribution.PackageId, (TContract)contribution.Instance))
                    .ToArray()
                : [];
        }
    }

    private sealed record RuntimePackageExtensionContribution(string PackageId, object Instance);

    private void RaiseChanged(PackageExtensionCatalogChangeReason reason, IReadOnlyList<PackageExtensionChange> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }

        var revision = Interlocked.Increment(ref _revision);
        var args = new PackageExtensionCatalogChangedEventArgs(revision, reason, changes);
        ExtensionsChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, args);
    }
}
