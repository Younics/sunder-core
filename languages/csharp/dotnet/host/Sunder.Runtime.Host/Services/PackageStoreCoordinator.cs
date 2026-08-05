using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageStoreCoordinator
{
    private readonly RuntimePackagePaths _paths;
    private readonly PackageStoreStageManager _stages;
    private readonly PackageStoreTransactionManager _transactions;
    private readonly PackageStoreRecovery _recovery;
    private long _catalogGeneration;

    public PackageStoreCoordinator(
        RuntimePackagePaths paths,
        InstalledPackageStore store,
        SunderPackageArchiveInstaller archiveInstaller,
        IPackageStoreFaultInjector? faultInjector = null)
    {
        _paths = paths;
        _stages = new PackageStoreStageManager(store, archiveInstaller);
        _transactions = new PackageStoreTransactionManager(paths, store, faultInjector);
        _recovery = new PackageStoreRecovery(paths, store, _transactions);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _recovery.InitializeAsync(cancellationToken);
        Interlocked.Increment(ref _catalogGeneration);
    }

    public Task<PackageStoreStagePreparation> PrepareStageAsync(
        IReadOnlyList<PackageStoreMutation> mutations,
        CancellationToken cancellationToken = default) =>
        _stages.PrepareAsync(mutations, CatalogGeneration, cancellationToken);

    public async Task<PackageOperationResult> CommitStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
    {
        if (!_stages.TryTake(stageId, out var stage))
        {
            return PackageOperationResults.Failure($"Package store stage '{stageId}' was not found.");
        }

        if (stage.BaseCatalogGeneration != CatalogGeneration)
        {
            PackageStoreStageManager.Cleanup(stage.PreparedArchives);
            return PackageOperationResults.Failure(
                $"Package store stage '{stageId}' is stale because the installed package catalog changed before commit.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            PackageStoreStageManager.Cleanup(stage.PreparedArchives);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var result = await _transactions.CommitAsync(stage, cancellationToken);
        if (result.Success)
        {
            Interlocked.Increment(ref _catalogGeneration);
        }

        return result;
    }

    public Task<bool> DiscardStageAsync(string stageId) => Task.FromResult(_stages.Discard(stageId));

    public async Task<PackageOperationResult> ExecuteAsync(
        IReadOnlyList<PackageStoreMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        var preparation = await PrepareStageAsync(mutations, cancellationToken);
        return preparation.Stage is null
            ? preparation.Failure ?? PackageOperationResults.Failure("Package store transaction could not be prepared.")
            : await CommitStageAsync(preparation.Stage.StageId, cancellationToken);
    }

    public Task DiscardAllStagesAsync()
    {
        _stages.DiscardAll();
        return Task.CompletedTask;
    }

    internal long CatalogGeneration => Interlocked.Read(ref _catalogGeneration);

    internal RuntimePackagePaths Paths => _paths;
}
