using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryPackageChangeOrchestrator(
    RegistryPackagePlanResolver planResolver,
    RuntimeContentTransferStore transferStore,
    RegistryPackageArtifactDownloader artifactDownloader,
    InstalledPackageLifecycleService installedPackages,
    ILogger<RegistryPackageChangeOrchestrator> logger)
{
    public async Task<RegistryResolveInstallPlanResponse> ResolveAsync(
        RuntimeRegistryPackageBatchRequest request,
        CancellationToken cancellationToken)
        => await planResolver.ResolveAsync(request, cancellationToken);

    public Task<RuntimeRegistryPackageChangeResult> InstallAsync(
        RuntimeRegistryPackageRequest request,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            new RuntimeRegistryPackageBatchRequest(
                request.RegistryOrigin,
                [new RegistryPackageChangeRequest(request.PackageId, request.Version, request.Version is null ? request.Tag : null)],
                request.IncludePrerelease,
                request.AllowDowngrade,
                request.Reinstall),
            cancellationToken);

    public async Task<RuntimeRegistryPackageChangeResult> UpdateAsync(
        RuntimeRegistryUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var installed = await installedPackages.GetInstalledAsync(cancellationToken);
        var selected = string.IsNullOrWhiteSpace(request.PackageId)
            ? installed
            : installed.Where(package => string.Equals(package.PackageId, request.PackageId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Count == 0)
        {
            return Failed(
                string.IsNullOrWhiteSpace(request.PackageId) ? "No packages are installed." : $"Package '{request.PackageId}' is not installed.",
                RuntimeRegistryErrorCode.NotFound);
        }

        return await ExecuteAsync(
            new RuntimeRegistryPackageBatchRequest(
                request.RegistryOrigin,
                selected.Select(package => new RegistryPackageChangeRequest(package.PackageId, null, "latest")).ToArray(),
                request.IncludePrerelease),
            cancellationToken);
    }

    public async Task<RuntimeRegistryPackageChangeResult> ExecuteAsync(
        RuntimeRegistryPackageBatchRequest request,
        CancellationToken cancellationToken)
    {
        RegistryResolveInstallPlanResponse plan;
        Uri origin;
        try
        {
            var resolution = await planResolver.ResolveForExecutionAsync(request, cancellationToken);
            origin = resolution.Origin;
            plan = resolution.Plan;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Registry package plan failed: {ErrorType}", ex.GetType().Name);
            return Failed(ex.Message, RuntimeRegistryErrorCode.RegistryUnavailable);
        }

        if (!plan.Success)
        {
            var errors = plan.Errors.Concat(plan.Conflicts.Select(conflict => conflict.Message)).DefaultIfEmpty("Package plan resolution failed.").ToArray();
            return new RuntimeRegistryPackageChangeResult(false, RuntimeRegistryErrorCode.Conflict, errors[0], false, false, plan.Warnings, errors, [], plan.Items);
        }

        if (plan.Items.Count == 0)
        {
            return new RuntimeRegistryPackageChangeResult(true, RuntimeRegistryErrorCode.None, "No package changes required.", true, false, plan.Warnings, [], [], []);
        }

        var uploadIds = new List<string>();
        string? stageId = null;
        try
        {
            var mutations = new List<PackageStoreMutationRequest>(plan.Items.Count);
            foreach (var item in plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var upload = await artifactDownloader.DownloadAsync(origin, item, cancellationToken);
                uploadIds.Add(upload.UploadId);

                mutations.Add(new PackageStoreMutationRequest(
                    item.CurrentVersion is null ? PackageStoreMutationKind.Install : PackageStoreMutationKind.Upgrade,
                    item.CurrentVersion is null ? null : item.PackageId,
                    upload.UploadId,
                    request.AllowDowngrade,
                    request.Reinstall));
            }

            var stage = await installedPackages.StageAsync(new PackageStoreStageRequest(mutations), cancellationToken);
            stageId = stage.StageId;
            if (!stage.Success || stageId is null)
            {
                var errors = stage.Errors.DefaultIfEmpty(stage.OperationResult.Message ?? "Package transaction staging failed.").ToArray();
                return new RuntimeRegistryPackageChangeResult(false, RuntimeRegistryErrorCode.Conflict, errors[0], false, false, plan.Warnings.Concat(stage.Warnings).ToArray(), errors, stage.ImpactedPackageIds, plan.Items);
            }

            var commit = await installedPackages.CommitStageAsync(stageId, cancellationToken);
            stageId = null;
            var commitErrors = commit.Errors.DefaultIfEmpty(commit.Message ?? "Package transaction commit failed.").ToArray();
            return new RuntimeRegistryPackageChangeResult(
                commit.Success,
                commit.Success ? RuntimeRegistryErrorCode.None : RuntimeRegistryErrorCode.InternalError,
                commit.Message ?? (commit.Success ? $"Applied {plan.Items.Count} package change(s)." : commitErrors[0]),
                commit.RuntimeSessionApplied,
                commit.RequiresAppRestart,
                plan.Warnings.Concat(commit.Warnings).ToArray(),
                commit.Success ? [] : commitErrors,
                commit.ImpactedPackageIds,
                plan.Items);
        }
        catch (OperationCanceledException)
        {
            if (stageId is not null)
            {
                await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            }
            throw;
        }
        catch (InvalidDataException ex)
        {
            if (stageId is not null)
            {
                await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            }
            return Failed(ex.Message, RuntimeRegistryErrorCode.ArtifactVerificationFailed, plan);
        }
        catch (RegistryArtifactTooLargeException ex)
        {
            if (stageId is not null) await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            return Failed(ex.Message, RuntimeRegistryErrorCode.DownloadTooLarge, plan);
        }
        catch (Exception ex)
        {
            if (stageId is not null)
            {
                await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            }
            logger.LogWarning("Registry package transaction failed: {ErrorType}", ex.GetType().Name);
            return Failed(ex.Message, RuntimeRegistryErrorCode.RegistryUnavailable, plan);
        }
        finally
        {
            foreach (var uploadId in uploadIds)
            {
                transferStore.DiscardUpload(uploadId);
            }
        }
    }

    private static RuntimeRegistryPackageChangeResult Failed(
        string message,
        RuntimeRegistryErrorCode errorCode,
        RegistryResolveInstallPlanResponse? plan = null)
        => new(false, errorCode, message, false, false, plan?.Warnings ?? [], [message], [], plan?.Items ?? []);

}
