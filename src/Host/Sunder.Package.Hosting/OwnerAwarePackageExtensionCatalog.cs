using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Hosting;

internal class OwnerAwarePackageExtensionCatalog :
    IPackageExtensionCatalog,
    IPackageExtensionInvocationCatalog,
    IPackageExtensionCatalogMonitor
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<ExtensionPointIdentity, List<OwnedContribution>> _extensions = [];
    private readonly Dictionary<string, OwnerActivationState> _currentOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<OwnerActivationState> _owners = [];
    private CatalogBatchState? _activeBatch;
    private long _revision;

    public event EventHandler<PackageExtensionCatalogChangedEventArgs>? Changed;

    public void Add<TContract>(string packageId, PackageExtensionPoint<TContract> extensionPoint, TContract contribution)
    {
        ValidatePackageId(packageId);
        PackageExtensionOwnerActivation owner;
        lock (_syncRoot)
        {
            owner = new PackageExtensionOwnerActivation(GetOrCreateOwnerLocked(packageId));
        }
        Add(owner, extensionPoint, contribution);
    }

    internal PackageExtensionOwnerActivation BeginOwnerActivation(string packageId)
    {
        ValidatePackageId(packageId);
        lock (_syncRoot)
        {
            if (_currentOwners.TryGetValue(packageId, out var current) && !current.IsRetiring)
            {
                throw new InvalidOperationException($"Package '{packageId}' already has an active extension owner epoch.");
            }

            var owner = new OwnerActivationState(this, packageId);
            _currentOwners[packageId] = owner;
            _owners.Add(owner);
            return new PackageExtensionOwnerActivation(owner);
        }
    }

    internal void Add<TContract>(
        PackageExtensionOwnerActivation owner,
        PackageExtensionPoint<TContract> extensionPoint,
        TContract contribution)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var identity = GetIdentity(extensionPoint);
        ArgumentNullException.ThrowIfNull(contribution);

        PackageExtensionCatalogChangedEventArgs? change;
        lock (_syncRoot)
        {
            ValidateActiveOwnerLocked(owner.State);
            var ownedContribution = new OwnedContribution(owner.State, contribution);
            if (_activeBatch is { } batch && ReferenceEquals(batch.Owner, owner.State))
            {
                batch.Contributions.Add(new StagedContribution(identity, ownedContribution));
                change = null;
            }
            else
            {
                PublishContributionLocked(identity, ownedContribution);
                change = CreateChangedEventArgsLocked(
                    PackageExtensionCatalogChangeReason.PackageActivated,
                    [new PackageExtensionChange(owner.State.PackageId, identity.Id, PackageExtensionChangeKind.Added, contribution.GetType())]);
            }
        }

        RaiseChanged(change);
    }

    public void RemovePackage(
        string packageId,
        PackageExtensionCatalogChangeReason reason = PackageExtensionCatalogChangeReason.PackageDeactivated)
        => _ = BeginPackageRetirement(packageId, reason);

    internal PackageExtensionOwnerRetirement BeginPackageRetirement(
        string packageId,
        PackageExtensionCatalogChangeReason reason = PackageExtensionCatalogChangeReason.PackageDeactivated)
    {
        ValidatePackageLookupId(packageId);
        OwnerActivationState? owner;
        lock (_syncRoot)
        {
            _currentOwners.TryGetValue(packageId, out owner);
        }

        return owner is null
            ? PackageExtensionOwnerRetirement.Completed(packageId)
            : BeginOwnerRetirement(new PackageExtensionOwnerActivation(owner), reason);
    }

    internal PackageExtensionOwnerRetirement BeginOwnerRetirement(
        PackageExtensionOwnerActivation owner,
        PackageExtensionCatalogChangeReason reason = PackageExtensionCatalogChangeReason.PackageDeactivated)
    {
        ArgumentNullException.ThrowIfNull(owner);
        OwnerRetirementTransition transition;
        lock (_syncRoot)
        {
            transition = BeginOwnerRetirementLocked(owner.State, reason);
        }

        if (transition.StartsRetirement)
        {
            transition.Owner.StartRetirement();
        }
        RaiseChanged(transition.Change);
        return transition.Retirement;
    }

    internal IReadOnlyList<PackageExtensionOwnerRetirement> BeginAllOwnerRetirements(
        PackageExtensionCatalogChangeReason reason = PackageExtensionCatalogChangeReason.PackageDeactivated)
    {
        OwnerRetirementTransition[] transitions;
        lock (_syncRoot)
        {
            transitions = _owners
                .Select(owner => BeginOwnerRetirementLocked(owner, reason))
                .ToArray();
        }

        foreach (var transition in transitions)
        {
            if (transition.StartsRetirement)
            {
                transition.Owner.StartRetirement();
            }
        }
        foreach (var transition in transitions)
        {
            RaiseChanged(transition.Change);
        }
        return transitions.Select(static transition => transition.Retirement).ToArray();
    }

    private OwnerRetirementTransition BeginOwnerRetirementLocked(
        OwnerActivationState owner,
        PackageExtensionCatalogChangeReason reason)
    {
        ValidateOwnedOwnerLocked(owner);
        if (owner.IsRetiring)
        {
            return new OwnerRetirementTransition(
                owner,
                new PackageExtensionOwnerRetirement(owner.PackageId, owner.Completion),
                StartsRetirement: false,
                Change: null);
        }

        owner.IsRetiring = true;
        if (_activeBatch is { } activeBatch && ReferenceEquals(activeBatch.Owner, owner))
        {
            foreach (var contribution in activeBatch.Contributions)
            {
                contribution.Contribution.Instance = null;
            }
            activeBatch.Contributions.Clear();
        }
        if (_currentOwners.TryGetValue(owner.PackageId, out var current)
            && ReferenceEquals(current, owner))
        {
            _currentOwners.Remove(owner.PackageId);
        }

        var changes = new List<PackageExtensionChange>();
        foreach (var identity in _extensions.Keys.ToArray())
        {
            var contributions = _extensions[identity];
            var removedContributions = contributions
                .Where(contribution => ReferenceEquals(contribution.Owner, owner))
                .ToArray();
            foreach (var removedContribution in removedContributions)
            {
                changes.Add(new PackageExtensionChange(
                    removedContribution.Owner.PackageId,
                    identity.Id,
                    PackageExtensionChangeKind.Removed,
                    removedContribution.Instance!.GetType()));
                removedContribution.IsPublished = false;
                removedContribution.Instance = null;
            }

            contributions.RemoveAll(contribution => ReferenceEquals(contribution.Owner, owner));
            if (contributions.Count == 0)
            {
                _extensions.Remove(identity);
            }
        }

        var change = CreateChangedEventArgsLocked(reason, changes);
        SignalOwnerDrainedLocked(owner);
        return new OwnerRetirementTransition(
            owner,
            new PackageExtensionOwnerRetirement(owner.PackageId, owner.Completion),
            StartsRetirement: true,
            change);
    }

    public IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
    {
        var identity = GetIdentity(extensionPoint);
        lock (_syncRoot)
        {
            return _extensions.TryGetValue(identity, out var contributions)
                ? contributions.Select(contribution => (TContract)contribution.Instance!).ToArray()
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
                        (TContract)contribution.Instance!))
                    .ToArray()
                : [];
        }
    }

    public IReadOnlyList<IPackageExtensionReference<TContract>> GetExtensionReferences<TContract>(
        PackageExtensionPoint<TContract> extensionPoint)
    {
        var identity = GetIdentity(extensionPoint);
        lock (_syncRoot)
        {
            return _extensions.TryGetValue(identity, out var contributions)
                ? contributions
                    .Select(contribution => (IPackageExtensionReference<TContract>)new ExtensionReference<TContract>(this, contribution))
                    .ToArray()
                : [];
        }
    }

    public virtual bool TryReportInvariantViolation<TContract>(
        IPackageExtensionReference<TContract> reference,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(exception);
        return false;
    }

    public PackageExtensionCatalogBatch BeginBatch(
        PackageExtensionOwnerActivation owner,
        PackageExtensionCatalogChangeReason reason)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_syncRoot)
        {
            ValidateActiveOwnerLocked(owner.State);
            if (_activeBatch is not null)
            {
                throw new InvalidOperationException("An extension catalog publication batch is already active.");
            }

            var batch = new CatalogBatchState(owner.State, reason);
            _activeBatch = batch;
            return new PackageExtensionCatalogBatch(this, batch);
        }
    }

    private void CompleteBatch(CatalogBatchState batch, bool publish)
    {
        PackageExtensionCatalogChangedEventArgs? change = null;
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_activeBatch, batch))
            {
                throw new InvalidOperationException("The extension catalog publication batch is not active.");
            }
            _activeBatch = null;
            if (publish)
            {
                try
                {
                    ValidateActiveOwnerLocked(batch.Owner);
                    var changes = new List<PackageExtensionChange>(batch.Contributions.Count);
                    foreach (var staged in batch.Contributions)
                    {
                        var instance = staged.Contribution.Instance
                            ?? throw new InvalidOperationException("A staged extension contribution was discarded before commit.");
                        PublishContributionLocked(staged.Identity, staged.Contribution);
                        changes.Add(new PackageExtensionChange(
                            batch.Owner.PackageId,
                            staged.Identity.Id,
                            PackageExtensionChangeKind.Added,
                            instance.GetType()));
                    }
                    change = CreateChangedEventArgsLocked(batch.Reason, changes);
                }
                catch
                {
                    DiscardBatchContributions(batch);
                    throw;
                }
            }
            else
            {
                DiscardBatchContributions(batch);
            }
        }

        RaiseChanged(change);
    }

    private void PublishContributionLocked(ExtensionPointIdentity identity, OwnedContribution contribution)
    {
        if (!_extensions.TryGetValue(identity, out var contributions))
        {
            contributions = [];
            _extensions[identity] = contributions;
        }
        contribution.IsPublished = true;
        contributions.Add(contribution);
    }

    private static void DiscardBatchContributions(CatalogBatchState batch)
    {
        foreach (var staged in batch.Contributions)
        {
            if (!staged.Contribution.IsPublished)
            {
                staged.Contribution.Instance = null;
            }
        }
        batch.Contributions.Clear();
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

    private OwnerActivationState GetOrCreateOwnerLocked(string packageId)
    {
        if (_currentOwners.TryGetValue(packageId, out var owner) && !owner.IsRetiring)
        {
            return owner;
        }

        owner = new OwnerActivationState(this, packageId);
        _currentOwners[packageId] = owner;
        _owners.Add(owner);
        return owner;
    }

    private void ValidateActiveOwnerLocked(OwnerActivationState owner)
    {
        ValidateOwnedOwnerLocked(owner);
        if (owner.IsRetiring
            || !_currentOwners.TryGetValue(owner.PackageId, out var current)
            || !ReferenceEquals(current, owner))
        {
            throw new InvalidOperationException($"Package '{owner.PackageId}' extension owner epoch is retiring.");
        }
    }

    private void ValidateOwnedOwnerLocked(OwnerActivationState owner)
    {
        if (!ReferenceEquals(owner.Catalog, this) || !_owners.Contains(owner))
        {
            throw new InvalidOperationException("The extension owner epoch does not belong to this catalog.");
        }
    }

    private bool TryAcquire<TContract>(OwnedContribution contribution, out IPackageExtensionLease<TContract>? lease)
    {
        lock (_syncRoot)
        {
            if (contribution.Owner.IsRetiring
                || !contribution.IsPublished
                || contribution.Instance is not TContract instance)
            {
                lease = null;
                return false;
            }

            contribution.Owner.ActiveLeaseCount++;
            lease = new ExtensionLease<TContract>(this, contribution.Owner, instance);
            return true;
        }
    }

    protected bool TryResolveActiveOwner<TContract>(
        IPackageExtensionReference<TContract> reference,
        [NotNullWhen(true)] out PackageExtensionOwnerToken? ownerToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        lock (_syncRoot)
        {
            if (reference is not ExtensionReference<TContract> ownedReference
                || !ReferenceEquals(ownedReference.Catalog, this))
            {
                ownerToken = null;
                return false;
            }

            var contribution = ownedReference.Contribution;
            var owner = contribution.Owner;
            if (owner.IsRetiring
                || !contribution.IsPublished
                || contribution.Instance is not TContract
                || !_currentOwners.TryGetValue(owner.PackageId, out var current)
                || !ReferenceEquals(current, owner))
            {
                ownerToken = null;
                return false;
            }

            ownerToken = new PackageExtensionOwnerToken(this, owner);
            return true;
        }
    }

    internal bool IsActiveOwner(PackageExtensionOwnerToken ownerToken)
    {
        ArgumentNullException.ThrowIfNull(ownerToken);
        lock (_syncRoot)
        {
            var owner = ownerToken.State;
            return ReferenceEquals(ownerToken.Catalog, this)
                   && ReferenceEquals(owner.Catalog, this)
                   && !owner.IsRetiring
                   && _owners.Contains(owner)
                   && _currentOwners.TryGetValue(owner.PackageId, out var current)
                   && ReferenceEquals(current, owner);
        }
    }

    private void Release(OwnerActivationState owner)
    {
        lock (_syncRoot)
        {
            if (owner.ActiveLeaseCount <= 0)
            {
                return;
            }

            owner.ActiveLeaseCount--;
            SignalOwnerDrainedLocked(owner);
        }
    }

    private static void SignalOwnerDrainedLocked(OwnerActivationState owner)
    {
        if (owner.IsRetiring && owner.ActiveLeaseCount == 0)
        {
            owner.LeasesDrained.TrySetResult();
        }
    }

    internal readonly record struct ExtensionPointIdentity(string Id, Type ContractType)
    {
        public bool Equals(ExtensionPointIdentity other)
            => ContractType == other.ContractType
               && string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode()
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Id), ContractType);
    }

    internal sealed class OwnedContribution(OwnerActivationState owner, object instance)
    {
        public OwnerActivationState Owner { get; } = owner;
        public string PackageId => Owner.PackageId;
        public object? Instance { get; set; } = instance;
        public bool IsPublished { get; set; }
    }

    internal sealed class CatalogBatchState(
        OwnerActivationState owner,
        PackageExtensionCatalogChangeReason reason)
    {
        public OwnerActivationState Owner { get; } = owner;
        public PackageExtensionCatalogChangeReason Reason { get; } = reason;
        public List<StagedContribution> Contributions { get; } = [];
    }

    internal sealed record StagedContribution(ExtensionPointIdentity Identity, OwnedContribution Contribution);

    private sealed record OwnerRetirementTransition(
        OwnerActivationState Owner,
        PackageExtensionOwnerRetirement Retirement,
        bool StartsRetirement,
        PackageExtensionCatalogChangedEventArgs? Change);

    internal sealed class OwnerActivationState
    {
        private readonly CancellationTokenSource _retirement = new();
        private readonly TaskCompletionSource<Task> _cancellationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OwnerActivationState(OwnerAwarePackageExtensionCatalog catalog, string packageId)
        {
            Catalog = catalog;
            PackageId = packageId;
            Completion = CompleteAsync(_cancellationStarted.Task);
        }

        public OwnerAwarePackageExtensionCatalog Catalog { get; }
        public string PackageId { get; }
        public CancellationToken RetirementToken => _retirement.Token;
        public TaskCompletionSource LeasesDrained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion { get; }
        public int ActiveLeaseCount { get; set; }
        public bool IsRetiring { get; set; }

        public void StartRetirement()
        {
            Task cancellation;
            try
            {
                cancellation = _retirement.CancelAsync();
            }
            catch (Exception exception)
            {
                cancellation = Task.FromException(exception);
            }
            _cancellationStarted.TrySetResult(cancellation);
        }

        private async Task CompleteAsync(Task<Task> cancellationStarted)
        {
            try
            {
                try
                {
                    await await cancellationStarted.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Trace.WriteLine($"Package extension owner retirement cancellation failed: {exception}");
                }

                await LeasesDrained.Task.ConfigureAwait(false);
            }
            finally
            {
                _retirement.Dispose();
            }
        }
    }

    private sealed class ExtensionReference<TContract>(
        OwnerAwarePackageExtensionCatalog catalog,
        OwnedContribution contribution) : IPackageExtensionReference<TContract>
    {
        internal OwnerAwarePackageExtensionCatalog Catalog { get; } = catalog;

        internal OwnedContribution Contribution { get; } = contribution;

        public bool TryAcquire([NotNullWhen(true)] out IPackageExtensionLease<TContract>? lease)
            => Catalog.TryAcquire(Contribution, out lease);
    }

    private sealed class ExtensionLease<TContract>(
        OwnerAwarePackageExtensionCatalog catalog,
        OwnerActivationState owner,
        TContract contribution) : IPackageExtensionLease<TContract>
    {
        private object? _contribution = contribution;

        public string PackageId
        {
            get
            {
                ThrowIfDisposed();
                return owner.PackageId;
            }
        }

        public TContract Contribution
            => (TContract)(Volatile.Read(ref _contribution)
                ?? throw new ObjectDisposedException(nameof(IPackageExtensionLease<TContract>)));

        public CancellationToken RetirementToken
        {
            get
            {
                ThrowIfDisposed();
                return owner.RetirementToken;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _contribution, null) is not null)
            {
                catalog.Release(owner);
            }
        }

        private void ThrowIfDisposed()
            => ObjectDisposedException.ThrowIf(Volatile.Read(ref _contribution) is null, this);
    }

    internal sealed class PackageExtensionCatalogBatch : IDisposable
    {
        private readonly OwnerAwarePackageExtensionCatalog _owner;
        private readonly CatalogBatchState _batch;
        private int _completed;

        internal PackageExtensionCatalogBatch(
            OwnerAwarePackageExtensionCatalog owner,
            CatalogBatchState batch)
        {
            _owner = owner;
            _batch = batch;
        }

        public void Commit()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                throw new InvalidOperationException("The extension catalog publication batch is already complete.");
            }
            _owner.CompleteBatch(_batch, publish: true);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _owner.CompleteBatch(_batch, publish: false);
            }
        }
    }
}

internal sealed class PackageExtensionOwnerActivation(OwnerAwarePackageExtensionCatalog.OwnerActivationState state)
{
    internal OwnerAwarePackageExtensionCatalog.OwnerActivationState State { get; } = state;
}

internal sealed class PackageExtensionOwnerToken(
    OwnerAwarePackageExtensionCatalog catalog,
    OwnerAwarePackageExtensionCatalog.OwnerActivationState state)
{
    internal OwnerAwarePackageExtensionCatalog Catalog { get; } = catalog;
    internal OwnerAwarePackageExtensionCatalog.OwnerActivationState State { get; } = state;
    public string PackageId => State.PackageId;
}

internal sealed class PackageExtensionOwnerRetirement(string packageId, Task completion)
{
    public string PackageId { get; } = packageId;
    public Task Completion { get; } = completion;

    public static PackageExtensionOwnerRetirement Completed(string packageId)
        => new(packageId, Task.CompletedTask);
}
