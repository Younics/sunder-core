using System.Reflection;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed class AppPackageCleanupUnprovenException(Guid generationId)
    : Exception(
        $"App package generation '{generationId}' cleanup could not be proven.");

internal sealed class AppPackageDataSettlementUnprovenException(Guid generationId)
    : Exception($"App package generation '{generationId}' resource cleanup completed, but data mutation settlement could not be proven.");

internal sealed class AppPackageGeneration(
    Guid id,
    string? folder,
    IReadOnlyList<ActivePackageDescriptor> activePackages,
    AppPackageViewRegistry viewRegistry,
    AppPackageHostState state,
    AppPackageHostComposition composition,
    PackageIconGeneration iconGeneration) : IAsyncDisposable
{
    private int _disposed;
    private int _activationReferences = 1;
    private AppPackageGeneration[] _retainedGroups = [];
    private readonly List<AppPackageRpcReplacement> _rpcReplacements = [];
    private readonly List<AppPackageGenerationPublication.DataHandoff> _dataHandoffs = [];
    private AppPackageGenerationDirectory? _directory = folder is null ? null : new(folder);
    internal IReadOnlyList<string> OwnedActivationIds { get; private set; } = [];
    internal void SealActivations() => OwnedActivationIds = State.SnapshotOwnedPackageIds();
    internal IEnumerable<AppPackageGeneration> ActivationGroups => _retainedGroups.Append(this);

    internal void RetainGroup(AppPackageGeneration owner, bool existingActivation = true)
    {
        Interlocked.Increment(ref owner._activationReferences);
        _retainedGroups = [.. _retainedGroups, owner];
        foreach (var packageId in owner.OwnedActivationIds)
        {
            State.RetainPackage(packageId, owner.State);
            ViewRegistry.RetainPackage(packageId, owner.ViewRegistry);
            Composition.RetainPackage(packageId, owner.Composition, existingActivation);
        }
    }

    internal void ShareDirectory(AppPackageGeneration owner) => _directory = owner._directory?.Retain();
    internal void PublishServices()
    {
        foreach (var group in ActivationGroups) group.Composition.PublishServices();
    }

    internal void AddRpcReplacement(AppPackageRpcReplacement replacement) => _rpcReplacements.Add(replacement);
    internal async Task PrepareDataHandoffsAsync(AppPackageGeneration previous, CancellationToken token)
    {
        var retained = ActivationGroups.ToHashSet();
        foreach (var group in previous.ActivationGroups.Where(group => !retained.Contains(group)))
        {
            var handoff = group.Composition.BeginDataHandoff();
            _dataHandoffs.Add(handoff);
        }
        // No candidate module/view may observe state until all writes accepted
        // by its predecessor have settled. Retained activations are not paused.
        await Task.WhenAll(_dataHandoffs.Select(handoff => handoff.Drained))
            .WaitAsync(AppShutdownBudgets.GenerationRetirement, token).ConfigureAwait(false);
    }
    internal void ReleaseDataHandoffs()
    {
        foreach (var handoff in _dataHandoffs) handoff.Dispose();
        _dataHandoffs.Clear();
    }
    internal void CommitDataHandoffs()
    {
        foreach (var handoff in _dataHandoffs) handoff.Commit();
        _dataHandoffs.Clear();
    }
    internal void CommitRetainedActivations()
    {
        foreach (var replacement in _rpcReplacements) replacement.Commit();
        _rpcReplacements.Clear();
    }
    internal void RevokeUnretainedServices()
    {
        foreach (var group in ActivationGroups)
            if (Volatile.Read(ref group._activationReferences) == 1) group.Composition.UnpublishServices();
    }

    public Guid Id { get; } = id;
    internal RuntimePackageStamp? RuntimeStamp { get; set; }

    public string? Folder { get; } = folder;

    public IReadOnlyList<ActivePackageDescriptor> ActivePackages { get; } = activePackages;

    public AppPackageViewRegistry ViewRegistry { get; } = viewRegistry;

    public AppPackageHostState State { get; } = state;

    public AppPackageHostComposition Composition { get; } = composition;

    public PackageIconGeneration IconGeneration { get; private set; } = iconGeneration;

    public AppPackageQuarantineContext QuarantineContext { get; set; } =
        AppPackageQuarantineContext.Empty;

    public IReadOnlyList<(string PackageId, Assembly Assembly)> SnapshotPackageAssemblies()
        => ActivationGroups.SelectMany(group => group.Composition.AssemblyTracker.SnapshotPackageAssemblies()).ToArray();

    public PackageIconGeneration ReplaceIconGeneration(PackageIconGeneration replacement)
    {
        var previous = IconGeneration;
        IconGeneration = replacement;
        return previous;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            foreach (var replacement in _rpcReplacements) await replacement.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _rpcReplacements.Clear();
            ReleaseDataHandoffs();
            var retained = Interlocked.Exchange(ref _retainedGroups, []);
            // Even failed auxiliary cleanup must release this presentation's references;
            // it cannot keep another activation published after its actual owner retires.
            State.DetachRetainedPackages();
            ViewRegistry.DetachRetainedPackages();
            Composition.DetachRetainedPackages();
            try { IconGeneration.Dispose(); }
            finally { await Task.WhenAll(retained.Append(this).Select(group => group.ReleaseActivationsAsync())).ConfigureAwait(false); }
        }
    }

    private async Task ReleaseActivationsAsync()
    {
        if (Interlocked.Decrement(ref _activationReferences) != 0) return;
        Task<AppPackageUnloadResult>[] packageRetirements;
        try
        {
            var retirementDrains = Composition.BeginGenerationRetirement().ToList();
            packageRetirements = State
                .SnapshotLoadedPackageIds()
                .Concat(Composition.RetiringPackageIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(packageId => Task.Run(
                    () => Composition.UnloadPackageForGenerationRetirementAsync(packageId),
                    CancellationToken.None))
                .ToArray();
            retirementDrains.AddRange(packageRetirements);
            await Task.WhenAll(retirementDrains).ConfigureAwait(false);
        }
        finally { Composition.UnpublishServices(); }

        var cleanupProven = Composition.CleanupOutcome == AppPackageUiCleanupOutcome.Proven
            && packageRetirements.All(task =>
                task.Result.CleanupOutcome == AppPackageUiCleanupOutcome.Proven);
        cleanupProven &= await Composition.DisposeRemainingOwnedResourcesAsync()
            .ConfigureAwait(false) == AppPackageUiCleanupOutcome.Proven;
        Composition.DisposeSharedAssemblies();
        Composition.Dispose();
        if (cleanupProven) Interlocked.Exchange(ref _directory, null)?.Release();
        if (!cleanupProven)
        {
            throw new AppPackageCleanupUnprovenException(Id);
        }
        if (packageRetirements.Any(task => !task.Result.DataSettlementProven))
            throw new AppPackageDataSettlementUnprovenException(Id);
    }
}

// Presentation directories can contain several independent package activations.
// Keep their bytes until the last activation born there is actually retired.
internal sealed class AppPackageGenerationDirectory(string folder)
{
    private int _references = 1;
    internal AppPackageGenerationDirectory Retain() { Interlocked.Increment(ref _references); return this; }
    internal void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0) AppPackageSourcePreparer.TryDeleteDirectory(folder);
    }
}
