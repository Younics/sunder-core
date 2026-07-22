using System.Collections.ObjectModel;
using System.Diagnostics;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Hosting;

internal class OwnerAwarePackageExtensionCatalog : IPackageExtensionCatalog, IPackageExtensionCatalogMonitor
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<ExtensionPointIdentity, List<OwnedContribution>> _extensions = [];
    private List<PackageExtensionChange>? _bufferedChanges;
    private PackageExtensionCatalogChangeReason _bufferedReason;
    private long _revision;

    public event EventHandler<PackageExtensionCatalogChangedEventArgs>? Changed;

    public void Add<TContract>(string packageId, PackageExtensionPoint<TContract> extensionPoint, TContract contribution)
    {
        ValidatePackageId(packageId);
        var identity = GetIdentity(extensionPoint);
        ArgumentNullException.ThrowIfNull(contribution);

        PackageExtensionCatalogChangedEventArgs? change;
        lock (_syncRoot)
        {
            if (!_extensions.TryGetValue(identity, out var contributions))
            {
                contributions = [];
                _extensions[identity] = contributions;
            }

            contributions.Add(new OwnedContribution(packageId, contribution));
            change = RecordChangesLocked(
                PackageExtensionCatalogChangeReason.PackageActivated,
                [new PackageExtensionChange(packageId, identity.Id, PackageExtensionChangeKind.Added, contribution.GetType())]);
        }

        RaiseChanged(change);
    }

    public void RemovePackage(
        string packageId,
        PackageExtensionCatalogChangeReason reason = PackageExtensionCatalogChangeReason.PackageDeactivated)
    {
        ValidatePackageLookupId(packageId);
        PackageExtensionCatalogChangedEventArgs? change;
        lock (_syncRoot)
        {
            var changes = new List<PackageExtensionChange>();
            foreach (var identity in _extensions.Keys.ToArray())
            {
                var contributions = _extensions[identity];
                var removedContributions = contributions
                    .Where(contribution => string.Equals(contribution.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                foreach (var removedContribution in removedContributions)
                {
                    changes.Add(new PackageExtensionChange(
                        removedContribution.PackageId,
                        identity.Id,
                        PackageExtensionChangeKind.Removed,
                        removedContribution.Instance.GetType()));
                }

                contributions.RemoveAll(contribution =>
                    string.Equals(contribution.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
                if (contributions.Count == 0)
                {
                    _extensions.Remove(identity);
                }
            }

            change = RecordChangesLocked(reason, changes);
        }

        RaiseChanged(change);
    }

    public IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
    {
        var identity = GetIdentity(extensionPoint);
        lock (_syncRoot)
        {
            return _extensions.TryGetValue(identity, out var contributions)
                ? contributions.Select(contribution => (TContract)contribution.Instance).ToArray()
                : [];
        }
    }

    public IReadOnlyList<PackageExtensionContribution<TContract>> GetExtensionContributions<TContract>(
        PackageExtensionPoint<TContract> extensionPoint)
    {
        var identity = GetIdentity(extensionPoint);
        lock (_syncRoot)
        {
            return _extensions.TryGetValue(identity, out var contributions)
                ? contributions
                    .Select(contribution => new PackageExtensionContribution<TContract>(
                        contribution.PackageId,
                        (TContract)contribution.Instance))
                    .ToArray()
                : [];
        }
    }

    public PackageExtensionCatalogBatch BeginBatch(PackageExtensionCatalogChangeReason reason)
    {
        lock (_syncRoot)
        {
            if (_bufferedChanges is not null)
            {
                throw new InvalidOperationException("An extension catalog publication batch is already active.");
            }

            _bufferedReason = reason;
            _bufferedChanges = [];
            return new PackageExtensionCatalogBatch(this);
        }
    }

    private void CompleteBatch(bool publish)
    {
        PackageExtensionCatalogChangedEventArgs? change = null;
        lock (_syncRoot)
        {
            var changes = _bufferedChanges
                ?? throw new InvalidOperationException("The extension catalog publication batch is not active.");
            _bufferedChanges = null;
            if (publish)
            {
                change = CreateChangedEventArgsLocked(_bufferedReason, changes);
            }
        }

        RaiseChanged(change);
    }

    private PackageExtensionCatalogChangedEventArgs? RecordChangesLocked(
        PackageExtensionCatalogChangeReason reason,
        IReadOnlyList<PackageExtensionChange> changes)
    {
        if (changes.Count == 0)
        {
            return null;
        }

        if (_bufferedChanges is not null)
        {
            _bufferedChanges.AddRange(changes);
            return null;
        }

        return CreateChangedEventArgsLocked(reason, changes);
    }

    private PackageExtensionCatalogChangedEventArgs? CreateChangedEventArgsLocked(
        PackageExtensionCatalogChangeReason reason,
        IReadOnlyList<PackageExtensionChange> changes)
    {
        if (changes.Count == 0)
        {
            return null;
        }

        var snapshot = new ReadOnlyCollection<PackageExtensionChange>(changes.ToArray());
        return new PackageExtensionCatalogChangedEventArgs(++_revision, reason, snapshot);
    }

    private void RaiseChanged(PackageExtensionCatalogChangedEventArgs? change)
    {
        if (change is null)
        {
            return;
        }

        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<PackageExtensionCatalogChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, change);
            }
            catch (Exception exception)
            {
                try
                {
                    Trace.WriteLine($"Package extension catalog change subscriber failed: {exception}");
                }
                catch
                {
                    // Diagnostics must not let a subscriber failure escape publication.
                }
            }
        }
    }

    private static ExtensionPointIdentity GetIdentity<TContract>(PackageExtensionPoint<TContract> extensionPoint)
    {
        ArgumentNullException.ThrowIfNull(extensionPoint);
        if (string.IsNullOrWhiteSpace(extensionPoint.Id)
            || !string.Equals(extensionPoint.Id, extensionPoint.Id.Trim(), StringComparison.Ordinal)
            || extensionPoint.Id.Any(static character => char.IsControl(character)))
        {
            throw new ArgumentException(
                "Extension point ids must be non-empty, trimmed, and contain no control characters.",
                nameof(extensionPoint));
        }

        return new ExtensionPointIdentity(extensionPoint.Id, typeof(TContract));
    }

    private static void ValidatePackageId(string packageId)
    {
        if (!PackageId.TryParse(packageId, out _))
        {
            throw new ArgumentException("Extension contribution ownership requires a canonical package id.", nameof(packageId));
        }
    }

    private static void ValidatePackageLookupId(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId)
            || !PackageId.TryParse(packageId.ToLowerInvariant(), out _))
        {
            throw new ArgumentException("Extension contribution removal requires a valid package id.", nameof(packageId));
        }
    }

    private readonly record struct ExtensionPointIdentity(string Id, Type ContractType)
    {
        public bool Equals(ExtensionPointIdentity other)
            => ContractType == other.ContractType
               && string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode()
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Id), ContractType);
    }

    private sealed record OwnedContribution(string PackageId, object Instance);

    internal sealed class PackageExtensionCatalogBatch(OwnerAwarePackageExtensionCatalog owner) : IDisposable
    {
        private int _completed;
        private bool _publish;

        public void Commit() => _publish = true;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                owner.CompleteBatch(_publish);
            }
        }
    }
}
