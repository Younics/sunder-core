using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed partial class AppPackageDeveloperServices
{
    async Task<DevPackageOwnerReloadResult> IAppPackageDevelopmentBackend.ReloadOwnerAsync(IDevPackageOwnerSession owner,
        string operationId, string packageId, string sourceContentIdentity, CancellationToken token)
    {
        var packageHost = _packageHost ?? throw new InvalidOperationException("Development reload requires the owning App shell.");
        string? stageId = null;
        try
        {
            var result = await owner.ReloadStagedAsync(operationId, packageId, sourceContentIdentity, async (stage, cancellation) =>
            {
                stageId = stage.StageId;
                if (!stage.Success) throw new InvalidOperationException(string.Join(" | ", stage.Errors));
                await packageHost.RetireActivationsAsync(stage.Retirements, cancellation).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            stageId = result.StageId;
            if (packageHost.IsStopping || _launcher is not { } launcher)
                throw new InvalidOperationException("The App shell stopped before the acknowledged development reload could be reconciled.");
            using var client = clients.CreateClient<IRuntimeShellClient>();
            var consistent = await ShellStartupCoordinator.LoadConsistentPackageUiStateAsync(client, result.Snapshot,
                cancellation => client.GetActivePackageUiSnapshotsAsync(AppPackageTargetEnvironment.CurrentRid, cancellation),
                requiredRuntimeInstanceId: result.RuntimeInstanceId, cancellationToken: token).ConfigureAwait(false);
            if (consistent.Snapshot.SessionGeneration < result.Snapshot.SessionGeneration)
                throw new InvalidDataException("The Runtime returned a snapshot older than the acknowledged development reload.");
            await launcher.ReconcileDevelopmentReleaseAsync(consistent.Snapshot, consistent.PackageSources, token).ConfigureAwait(false);
            return result;
        }
        catch
        {
            // Restore the authoritative old or new view only after this exact stage
            // becomes terminal. Never compensate by replaying a build or load.
            if (stageId is not null) ((IAppPackageDevelopmentBackend)this).QueueRetirementReconciliation(stageId);
            throw;
        }
    }
}
