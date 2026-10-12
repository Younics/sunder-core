using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed partial class PackageOperationService
{
    public async Task<BackgroundProcessSnapshot?> EnqueueLocalInstallAsync(PackageArchiveSelection selection, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        IRuntimePackageChangeClient? client = null;
        PackageUploadRetentionSession? retained = null;
        var enqueued = false;
        var released = 0;
        try
        {
            if (selection.Review is not { IsValid: true } review || _confirmLocalPackage is null)
                throw new InvalidOperationException("Select the local package again to review its permissions before installing.");
            client = _runtimeApiClientFactory.CreateClient<IRuntimePackageChangeClient>();
            var upload = await client.UploadPackageAsync(selection.PackagePath, preparation.Token).ConfigureAwait(false);
            if (upload.ContentHash != selection.Sha256)
                throw new InvalidDataException("The package archive changed after review. Select it again before installing.");
            // Root/installation-owned retention outlives confirmation and queued work, including
            // cancellation before the package delegate ever runs. Page closure is not its owner.
            retained = await PackageUploadRetentionSession.AcquireAsync(client, upload, _lifetimeToken, _uploadRetentionClock).ConfigureAwait(false);
            using var reviewLifetime = CancellationTokenSource.CreateLinkedTokenSource(preparation.Token, retained.Token);
            var request = new PackageStoreStageRequest([new(PackageStoreMutationKind.Install, UploadId: upload.UploadId) { UploadRetention = retained.Handle }]);
            var source = new RuntimePackageRuntimePrerequisiteRequest(Local: request);
            var preview = await client.PreviewPackageRuntimePrerequisitesAsync(source, reviewLifetime.Token).ConfigureAwait(false);
            RegistryPackageInstallService.ValidateRuntimePreview(preview);
            var approval = new PackageInstallationApproval(preview);
            if (!await _confirmLocalPackage(review.WithRuntimes(preview), reviewLifetime.Token).WaitAsync(reviewLifetime.Token).ConfigureAwait(false)) return null;
            reviewLifetime.Token.ThrowIfCancellationRequested();

            async Task<RuntimePackageRuntimePrerequisitePlan> Refresh(CancellationToken token)
            {
                await retained.CheckAvailableAsync(token).ConfigureAwait(false);
                var current = await client.PreviewPackageRuntimePrerequisitesAsync(source with { ExpectedSourceFingerprint = preview.SourceFingerprint }, token).ConfigureAwait(false);
                approval.Validate(current);
                return current;
            }

            var members = preview.Requirements.Select(item => item.Package.PackageId).Append(review.PackageId).Distinct(StringComparer.Ordinal).ToArray();
            var metadata = new PackageOperationMetadata(review.PackageId, PackageOperationKind.InstallLocal, review.PackageName) { PackageIds = members };
            var installation = PackageInstallationCohort.Enqueue(_backgroundProcesses, $"Install {review.PackageName}", metadata.ToMetadata(),
                approval.NeedsRuntimeInstallation ? context => approval.InstallRuntimesAsync(client, Refresh, context) : null,
                async context =>
                {
                    var current = await Refresh(context.CancellationToken).ConfigureAwait(false);
                    approval.Validate(current, requireReady: true);
                    await retained.PrepareForConsumptionAsync(context.CancellationToken).ConfigureAwait(false);
                    var approved = new PackageStoreStageRequest(request.Mutations.Select(mutation => mutation with { CapabilityConsent = selection.CapabilityConsent }).ToArray())
                    { RuntimePrerequisiteExpectation = new(current.SourceFingerprint, current.Rid) };
                    context.ReportIndeterminate("Installing package…");
                    var result = await StageCommitPackageStoreAsync(client, approved, context.CancellationToken).ConfigureAwait(false);
                    if (!result.Success) throw new InvalidOperationException(result.Errors.FirstOrDefault() ?? result.Message);
                    await _operationFinalizer.FinishLocalOperationAsync(context, result, "Package installed", "Package installed from disk.").ConfigureAwait(false);
                }, retained.Token, Release, error => _operationFinalizer.ReportFailureAsync(error.Message), () => retained.Failure);
            enqueued = true;
            return installation.PackageJob;
        }
        finally { if (!enqueued) await Release().ConfigureAwait(false); }

        async ValueTask Release()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            try { if (retained is not null) await retained.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                client?.Dispose();
                if (selection.DeleteAfterUse) PackageArchivePicker.DeleteReviewSnapshot(selection.PackagePath);
            }
        }
    }
}
