using System.Security.Cryptography;
using Sunder.Runtime.Contracts;
using static Sunder.Runtime.Host.Services.ExecutionRuntimeManagementSupport;

namespace Sunder.Runtime.Host.Services;

internal sealed class ExecutionRuntimeManagementService(
    ExecutionRuntimeInstallationService installation,
    ExecutionRuntimeStore store,
    InstalledPackageStore installedPackageStore,
    InstalledPackageLifecycleService installedPackages,
    PackageProjectionMaintenance projectionMaintenance,
    ILogger<ExecutionRuntimeManagementService> logger,
    OfficialRuntimeResolver resolver,
    ExecutionRuntimeArtifactDownloader downloader,
    TimeProvider clock,
    RuntimeOperationGate operations,
    ExecutionRuntimeManagementState? managementState = null,
    RuntimeEventStreamService? events = null)
{
    public async Task<IReadOnlyList<InstalledExecutionRuntimeSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var receipts = await store.ListReceiptsAsync(cancellationToken).ConfigureAwait(false);
        var packages = await installedPackageStore.ListAsync(cancellationToken).ConfigureAwait(false);
        var summaries = new List<InstalledExecutionRuntimeSummary>(receipts.Count);
        foreach (var receipt in receipts)
            summaries.Add(await BuildSummaryAsync(receipt, packages, cancellationToken).ConfigureAwait(false));
        return summaries;
    }

    public async Task<InstalledExecutionRuntimeSummary> GetAsync(
        RuntimeExecutionRuntimeIdentity identity,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        var receipt = await store.GetReceiptAsync(Identity(identity), cancellationToken).ConfigureAwait(false)
                      ?? throw new RuntimeExecutionRuntimeException(
                          ExecutionRuntimeErrorCodes.Missing,
                          "The exact execution runtime is not installed.");
        var packages = await installedPackageStore.ListAsync(cancellationToken).ConfigureAwait(false);
        return await BuildSummaryAsync(receipt, packages, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RuntimeExecutionRuntimePackageReference>> GetReferencesAsync(
        RuntimeExecutionRuntimeIdentity identity,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        return (await installedPackageStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .SelectMany(package => package.ExecutionRuntimeBindings
                .Where(binding => BindingMatches(binding, identity))
                .Select(binding => new RuntimeExecutionRuntimePackageReference(
                    package.PackageId,
                    package.Version,
                    package.ContentIdentity,
                    binding.Role,
                    binding.Rid)))
            .OrderBy(static reference => reference.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static reference => reference.Role, StringComparer.Ordinal)
            .ThenBy(static reference => reference.Rid, StringComparer.Ordinal)
            .ToArray();
    }

    public Task<RuntimeExecutionRuntimeImpact> GetImpactAsync(
        RuntimeExecutionRuntimeIdentity identity,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        return installedPackages.GetExecutionRuntimeImpactAsync(Identity(identity), cancellationToken);
    }

    public async Task<IReadOnlyList<PackageTargetExecutionRuntimeBinding>> GetPackageBindingsAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        var package = (await installedPackages.GetInstalledAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(candidate.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
        if (package is null) throw new RuntimeNotFoundException($"Installed package '{packageId}' was not found.");
        return package.ExecutionRuntimeBindings.ToArray();
    }

    public async Task<RuntimeExecutionRuntimeOperationResult> EnsureAsync(
        RuntimeExecutionRuntimeEnsureRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequirement(request?.Requirement);
        var requirement = request!.Requirement;
        try
        {
            // GC captures package/manual reachability under this gate. Keep the exact root
            // pinned across final metadata confirmation, durable intent and response health.
            await using var operation = await operations.EnterAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken = operation.CancellationToken;
            if (managementState is not null) _ = await managementState.ReadAsync(cancellationToken).ConfigureAwait(false);
            var resolution = await installation.ResolveAsync(requirement, cancellationToken).ConfigureAwait(false);
            await using var retention = await store.RetainAsync(new(Identity(resolution.Artifact.Identity)), cancellationToken).ConfigureAwait(false);
            var ensured = await installation.AcquireAsync(resolution, cancellationToken).ConfigureAwait(false);
            if (request.RetainUntilUninstall && managementState is not null)
                await managementState.RetainAsync(new(Identity(ensured.Receipt), UpdateLine(Identity(ensured.Receipt))), CancellationToken.None)
                    .ConfigureAwait(false);

            var summary = await GetSummaryAsync(ensured.Receipt, cancellationToken).ConfigureAwait(false);
            return new RuntimeExecutionRuntimeOperationResult(
                true,
                null,
                ensured.AlreadyPresent
                    ? "Execution runtime is already installed and healthy."
                    : "Execution runtime installed and verified.",
                RuntimeExecutionRuntimeOperationActions.Ensure,
                summary,
                ensured.AlreadyPresent);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return MapFailure(RuntimeExecutionRuntimeOperationActions.Ensure, exception, requirement: requirement);
        }
    }

    public async Task<RuntimeExecutionRuntimeOperationResult> VerifyAsync(
        RuntimeExecutionRuntimeIdentity identity,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        var local = Identity(identity);
        var impact = await installedPackages.GetExecutionRuntimeImpactAsync(local, cancellationToken).ConfigureAwait(false);
        ExecutionRuntimeReceiptV1? receipt;
        try
        {
            receipt = await store.GetReceiptAsync(local, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
        {
            return Failed(RuntimeExecutionRuntimeOperationActions.Verify, ExecutionRuntimeErrorCodes.IntegrityFailed,
                "The execution runtime receipt failed integrity validation.", impact);
        }
        if (receipt is null)
            return Failed(RuntimeExecutionRuntimeOperationActions.Verify, ExecutionRuntimeErrorCodes.Missing,
                "The exact execution runtime is not installed.", impact);

        var verification = await store.VerifyAsync(local, deep: true, cancellationToken).ConfigureAwait(false);
        events?.PublishExecutionRuntimesChanged();
        var summary = await GetSummaryAsync(receipt, cancellationToken).ConfigureAwait(false);
        return verification.Healthy
            ? new(true, null, "Execution runtime passed deep verification.", RuntimeExecutionRuntimeOperationActions.Verify,
                summary, AlreadyPresent: true, impact)
            : new(false, ExecutionRuntimeErrorCodes.IntegrityFailed,
                "Execution runtime failed deep verification and requires repair.", RuntimeExecutionRuntimeOperationActions.Verify,
                summary, AlreadyPresent: true, impact);
    }

    public async Task<RuntimeExecutionRuntimeOperationResult> RepairAsync(
        RuntimeExecutionRuntimeIdentity identity,
        RuntimeExecutionRuntimeActionRequest request,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        if (request is null || !IsHash(request.ExpectedImpactFingerprint))
            throw new RuntimeValidationException("Execution runtime repair requires a valid confirmed impact fingerprint.");

        var local = Identity(identity);
        var impact = await installedPackages.GetExecutionRuntimeImpactAsync(local, cancellationToken).ConfigureAwait(false);
        try
        {
            ExecutionRuntimeReceiptV1? receipt;
            try { receipt = await store.GetReceiptAsync(local, cancellationToken).ConfigureAwait(false); }
            catch (InvalidDataException) { receipt = null; }
            var bindings = (await installedPackageStore.ListAsync(cancellationToken).ConfigureAwait(false))
                .SelectMany(package => package.ExecutionRuntimeBindings).Where(binding => binding.Runtime == identity).ToArray();
            var expected = receipt?.InstalledArtifact ?? bindings.FirstOrDefault()?.InstalledArtifact;
            if (expected is not null && bindings.Any(binding => binding.InstalledArtifact != expected))
                throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.BindingInvalid, "Committed package bindings disagree on exact runtime content.");
            if (receipt is null && bindings.Length == 0 && (managementState is null
                || !(await managementState.ReadAsync(cancellationToken).ConfigureAwait(false)).ManualInstalls.Any(value => value.Runtime == identity)))
                return Failed(RuntimeExecutionRuntimeOperationActions.Repair, ExecutionRuntimeErrorCodes.Missing,
                    "No installed binding or manual retention authorizes recovery of this exact runtime.", impact);

            var repair = await installedPackages.ExecuteExecutionRuntimeRepairAsync(
                    local,
                    request.ExpectedImpactFingerprint,
                    token => store.VerifyAsync(local, deep: true, token),
                     RecoverExactAsync,
                    cancellationToken)
                .ConfigureAwait(false);
            if (repair.Verification.Healthy)
                return new(true, null, "Execution runtime is already healthy.", RuntimeExecutionRuntimeOperationActions.Repair,
                    await GetSummaryAsync(receipt ?? await store.GetReceiptAsync(local, cancellationToken).ConfigureAwait(false)
                        ?? throw Stale(), cancellationToken).ConfigureAwait(false), AlreadyPresent: true, repair.Impact);
            if (repair.Repaired is null)
                return Failed(RuntimeExecutionRuntimeOperationActions.Repair, ExecutionRuntimeErrorCodes.Missing,
                    "The exact execution runtime is not installed.", repair.Impact);

            var reload = repair.Reload ?? throw new InvalidOperationException("Execution runtime repair completed without package reload evidence.");
            return new RuntimeExecutionRuntimeOperationResult(
                reload.Success,
                reload.Success ? null : ExecutionRuntimeErrorCodes.InstallFailed,
                reload.Success
                    ? "Execution runtime repaired and dependent packages reloaded."
                    : "Execution runtime repaired, but dependent packages could not be reloaded automatically.",
                RuntimeExecutionRuntimeOperationActions.Repair,
                await GetSummaryAsync(repair.Repaired.Receipt, cancellationToken).ConfigureAwait(false),
                AlreadyPresent: false,
                repair.Impact);

            async Task<ExecutionRuntimeEnsureResult?> RecoverExactAsync(CancellationToken token)
            {
                // Cached bytes are sufficient for exact repair. Vendor support observations are display
                // evidence, not an execution lease; loss of the network cannot invalidate a verified root.
                if (expected is not null && File.Exists(store.Layout.GetArchivePath(expected.ArchiveSha256)))
                {
                    var support = receipt?.Support ?? new RuntimeExecutionRuntimeSupportStatus(
                        RuntimeExecutionRuntimeSupportState.Unknown,
                        identity.SourceId == ConfiguredOfficialSourceId.DotNet ? "10.0 LTS" : "24 LTS",
                        clock.GetUtcNow(), null, "Support information was not refreshed during offline recovery.");
                    var cachedArtifact = new RuntimeExecutionRuntimeVendorArtifact(identity, expected.Provenance.Distribution, support);
                    try
                    {
                        return await store.EnsureAsync(new(cachedArtifact, expected.ArchiveSha256, expected.ArchiveSize, expected.ContentIdentity, expected.Provenance),
                            store.Layout.GetArchivePath(expected.ArchiveSha256), token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is InvalidDataException or CryptographicException
                        || error is RuntimeExecutionRuntimeException runtime && runtime.Code == ExecutionRuntimeErrorCodes.IntegrityFailed)
                    {
                        logger.LogWarning("Cached official runtime bytes failed verification; attempting exact vendor recovery.");
                    }
                }
                var artifact = await resolver.ResolveExactAsync(identity, token).ConfigureAwait(false);
                var result = await downloader.DownloadAsync(new ExecutionRuntimeAcquisitionPlan(artifact,
                    expected?.ArchiveSha256, expected?.ArchiveSize, expected?.ContentIdentity, expected?.Provenance), token).ConfigureAwait(false);
                var confirmation = await resolver.ResolveExactAsync(identity, token).ConfigureAwait(false);
                if (!Equals(OfficialRuntimeSourcePolicy.Selection(artifact), OfficialRuntimeSourcePolicy.Selection(confirmation)))
                    throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.PlanStale);
                return result;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return MapFailure(RuntimeExecutionRuntimeOperationActions.Repair, exception, impact: impact, identity: local);
        }
        finally { events?.PublishExecutionRuntimesChanged(); }
    }

    public async Task<RuntimeExecutionRuntimeOperationResult> RepairAsync(
        RuntimeExecutionRuntimeIdentity identity,
        CancellationToken cancellationToken)
    {
        var impact = await GetImpactAsync(identity, cancellationToken).ConfigureAwait(false);
        return await RepairAsync(identity, new(impact.Fingerprint), cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeExecutionRuntimeOperationResult> CollectGarbageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var runtimeResult = await store.CollectGarbageAsync(cancellationToken).ConfigureAwait(false);
            if (runtimeResult.RootsRemoved > 0 || runtimeResult.ArchivesRemoved > 0) events?.PublishExecutionRuntimesChanged();
            var projectionResult = await projectionMaintenance.CollectGarbageAsync(cancellationToken).ConfigureAwait(false);
            var message =
                $"Execution runtime GC removed {runtimeResult.RootsRemoved} roots and {runtimeResult.ArchivesRemoved} archives. "
                + $"Package projection GC recovered {projectionResult.RecoveredLeaseCount} stale leases, marked {projectionResult.MarkedEntryCount} entries, removed {projectionResult.TombstonedEntryCount} entries, and preserved {projectionResult.PreservedEntryCount} entries.";
            return new RuntimeExecutionRuntimeOperationResult(true, null, message,
                RuntimeExecutionRuntimeOperationActions.GarbageCollect, Runtime: null, AlreadyPresent: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return MapFailure(RuntimeExecutionRuntimeOperationActions.GarbageCollect, exception);
        }
    }

    private async Task<InstalledExecutionRuntimeSummary> GetSummaryAsync(
        ExecutionRuntimeReceiptV1 receipt,
        CancellationToken cancellationToken)
    {
        var packages = await installedPackageStore.ListAsync(cancellationToken).ConfigureAwait(false);
        return await BuildSummaryAsync(receipt, packages, cancellationToken).ConfigureAwait(false);
    }

    private async Task<InstalledExecutionRuntimeSummary> BuildSummaryAsync(
        ExecutionRuntimeReceiptV1 receipt,
        IReadOnlyList<InstalledPackageRecord> packages,
        CancellationToken cancellationToken)
    {
        var verification = await store.VerifyAsync(receipt.Identity, deep: false, cancellationToken).ConfigureAwait(false);
        var status = verification.Healthy ? RuntimeExecutionRuntimeStatuses.Ready : RuntimeExecutionRuntimeStatuses.Unhealthy;
        var referenceCount = packages.Count(package => package.ExecutionRuntimeBindings.Any(binding => BindingMatches(binding, receipt.Identity.ToContract())));
        return new InstalledExecutionRuntimeSummary(
            receipt.Identity.ToContract(),
            status,
            receipt.Content.Sum(static content => content.Size),
            referenceCount,
            receipt.InstalledArtifact,
            new RuntimeExecutionRuntimeHealth(
                status,
                verification.CheckedAtUtc,
                RepairRecommended: !verification.Healthy,
                verification.Healthy ? null : GetSafeMessage(ExecutionRuntimeErrorCodes.IntegrityFailed)),
            receipt.Support,
            receipt.InstalledAtUtc)
        {
            RetainedUntilUninstall = managementState is not null
                                    && (await managementState.ReadAsync(cancellationToken).ConfigureAwait(false)).ManualInstalls
                                    .Any(value => value.Runtime == receipt.Identity.ToContract()),
        };
    }

    private RuntimeExecutionRuntimeOperationResult MapFailure(
        string action,
        Exception exception,
        RuntimeExecutionRuntimeRequirement? requirement = null,
        RuntimeExecutionRuntimeImpact? impact = null,
        ExecutionRuntimeIdentity? identity = null)
    {
        var code = exception switch
        {
            RuntimeExecutionRuntimeException runtimeException => runtimeException.Code,
            ExecutionRuntimePlanMismatchException => ExecutionRuntimeErrorCodes.PlanStale,
            ExecutionRuntimeRootInUseException => ExecutionRuntimeErrorCodes.Busy,
            RuntimeWorkerCleanupUnprovenException => ExecutionRuntimeErrorCodes.Busy,
            HttpRequestException { StatusCode: null } or TimeoutException
                when action == RuntimeExecutionRuntimeOperationActions.Repair && identity is not null => ExecutionRuntimeErrorCodes.Offline,
            HttpRequestException or TimeoutException => ExecutionRuntimeErrorCodes.DownloadFailed,
            CryptographicException => ExecutionRuntimeErrorCodes.ArtifactInvalid,
            InvalidDataException when action == RuntimeExecutionRuntimeOperationActions.Repair => ExecutionRuntimeErrorCodes.IntegrityFailed,
            InvalidDataException => ExecutionRuntimeErrorCodes.ArtifactInvalid,
            ExecutionRuntimeStoreConflictException => ExecutionRuntimeErrorCodes.InstallFailed,
            IOException or UnauthorizedAccessException => ExecutionRuntimeErrorCodes.InstallFailed,
            _ => ExecutionRuntimeErrorCodes.InstallFailed,
        };
        logger.LogWarning(exception, "Execution runtime action {RuntimeAction} failed with {ErrorCode} ({ExceptionType})",
            action, code, exception.GetType().Name);
        var message = exception is RuntimeExecutionRuntimeException
            ? exception.Message
            : code == ExecutionRuntimeErrorCodes.DownloadFailed && requirement is not null
                ? $"Official runtime metadata or archives could not be reached while ensuring '{requirement.RuntimeId}' range '{requirement.VersionRange}' for RID '{Rid}'."
                : GetSafeMessage(code);
        return Failed(action, code, message, impact);
    }

    private static RuntimeExecutionRuntimeOperationResult Failed(
        string action,
        string code,
        string message,
        RuntimeExecutionRuntimeImpact? impact = null)
        => new(false, code, message, action, Runtime: null, AlreadyPresent: false, impact);

    private static string GetSafeMessage(string code)
        => ExecutionRuntimeErrorCatalog.All.Single(candidate => candidate.Code == code).SafeMessage;

    private static bool BindingMatches(InstalledPackageExecutionRuntimeBindingRecord binding, RuntimeExecutionRuntimeIdentity identity)
        => binding.Runtime == identity;

    private static PackageTargetExecutionRuntimeBinding ToContractBinding(InstalledPackageExecutionRuntimeBindingRecord binding)
        => new(binding.Role, binding.Rid, binding.Requirement, binding.Runtime, binding.InstalledArtifact, binding.ResolvedAtUtc);
}
