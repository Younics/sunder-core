using Microsoft.Extensions.Hosting;

namespace Sunder.Runtime.Host.Services;

/// <summary>Policy-only catalog updates use the same durable journal as package changes.</summary>
internal sealed class InstalledPackagePolicyCoordinator(InstalledPackageStore installed, PackageStoreCoordinator store,
    RuntimeOperationGate operations, PackageLifecyclePolicyCache policies, IHostApplicationLifetime? lifetime = null)
{
    private bool _indeterminate;
    internal InstalledPackageRecord ProjectCurrent(InstalledPackageRecord package) => policies.CaptureInstalled(package)?.ApplyTo(package) ?? package with { Policy = null };
    internal async Task ReconcileAsync(CancellationToken token)
    {
        await using var operation = await operations.EnterAsync(token);
        await ReconcileUnderOperationGateAsync(operation.CancellationToken);
    }

    internal async Task ReconcileUnderOperationGateAsync(CancellationToken token)
    {
        if (_indeterminate) throw new InvalidDataException("Installed policy persistence requires startup recovery.");
        var current = await installed.ListAsync(token);
        await policies.RestoreRetainedAsync(current.SelectMany(package => package.Policy?.Snapshots ?? []), token);
        await policies.LoadKnownOriginsAsync(token, checkpointClock: false);
        // A durable package cutover still owns its exact desired catalog. Its live
        // admission reads the recovered cache; persist newer display/binding policy
        // as a separate transaction after cutover acknowledgement.
        if (store.HasPreparedStages || await store.GetPendingRuntimeCutoverAsync(token) is not null) return;
        if (current.All(package => PackageStorePolicy.CatalogsEqual([package], [ProjectCurrent(package)]))) return;
        var desired = await policies.CaptureInstalledAsync(current, token);
        var changes = current.Zip(desired).Where(pair => !PackageStorePolicy.CatalogsEqual([pair.First], [pair.Second]))
            .Select(pair => (PackageStoreChange)new PackageLifecyclePolicyUpdateMutation(pair.First.PackageId, pair.First, pair.Second.Policy)).ToArray();
        if (changes.Length == 0) return;
        var preparation = await store.PrepareStageAsync(changes, token);
        if (!preparation.Success || preparation.Stage is null)
            throw new InvalidDataException(preparation.Failure?.Message ?? "Installed policy metadata could not be prepared.");
        var result = await store.CommitStageWithOutcomeAsync(preparation.Stage.StageId, token);
        if (result.Disposition == PackageStoreCommitDisposition.Indeterminate)
        {
            _indeterminate = true;
            lifetime?.StopApplication();
        }
        if (result.Disposition != PackageStoreCommitDisposition.Committed)
            throw new InvalidDataException(result.OperationResult.Message ?? "Installed policy metadata durability is unproven.");
    }
}
