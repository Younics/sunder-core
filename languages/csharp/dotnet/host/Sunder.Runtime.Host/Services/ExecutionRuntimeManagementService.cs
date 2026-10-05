using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed class ExecutionRuntimeManagementService(
    RegistryHttpClient registryClient,
    ExecutionRuntimeArtifactDownloader artifactDownloader,
    ExecutionRuntimeStore store,
    ExecutionRuntimeLifecyclePolicyCache lifecyclePolicies,
    InstalledPackageStore installedPackageStore,
    InstalledPackageLifecycleService installedPackages,
    RuntimeContentTransferService transfers,
    ExecutionRuntimeHostTrustService runtimeTrust,
    PackageProjectionMaintenance projectionMaintenance,
    TimeProvider timeProvider,
    ILogger<ExecutionRuntimeManagementService> logger,
    ExecutionRuntimePhaseTelemetry telemetry)
{
    public async Task<IReadOnlyList<InstalledExecutionRuntimeSummary>> ListAsync(
        CancellationToken cancellationToken)
    {
        var receipts = await store.ListReceiptsAsync(cancellationToken).ConfigureAwait(false);
        var packages = await installedPackageStore.ListAsync(cancellationToken).ConfigureAwait(false);
        await lifecyclePolicies.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var policies = lifecyclePolicies.CaptureSnapshot();
        var summaries = new List<InstalledExecutionRuntimeSummary>(receipts.Count);
        foreach (var receipt in receipts)
        {
            summaries.Add(await BuildSummaryAsync(receipt, packages, policies, cancellationToken)
                .ConfigureAwait(false));
        }
        return summaries;
    }

    public async Task<InstalledExecutionRuntimeSummary> GetAsync(
        string runtimeId,
        string version,
        int packagingRevision,
        string rid,
        string moduleAbi,
        CancellationToken cancellationToken)
    {
        var identity = CreateIdentity(runtimeId, version, packagingRevision, rid, moduleAbi);
        var receipt = await store.GetReceiptAsync(identity, cancellationToken).ConfigureAwait(false)
                      ?? throw new RuntimeExecutionRuntimeException(
                          ExecutionRuntimeErrorCodes.Missing,
                          "The exact execution runtime is not installed.");
        var packages = await installedPackageStore.ListAsync(cancellationToken).ConfigureAwait(false);
        await lifecyclePolicies.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await BuildSummaryAsync(
                receipt,
                packages,
                lifecyclePolicies.CaptureSnapshot(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RuntimeExecutionRuntimePackageReference>> GetReferencesAsync(
        string runtimeId,
        string version,
        int packagingRevision,
        string rid,
        string moduleAbi,
        CancellationToken cancellationToken)
    {
        var identity = CreateIdentity(runtimeId, version, packagingRevision, rid, moduleAbi);
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
        string runtimeId,
        string version,
        int packagingRevision,
        string rid,
        string moduleAbi,
        CancellationToken cancellationToken)
        => installedPackages.GetExecutionRuntimeImpactAsync(
            CreateIdentity(runtimeId, version, packagingRevision, rid, moduleAbi),
            cancellationToken);

    public async Task<IReadOnlyList<PackageTargetExecutionRuntimeBinding>> GetPackageBindingsAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        var package = (await installedPackages.GetInstalledAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(
                candidate.PackageId,
                packageId,
                StringComparison.OrdinalIgnoreCase));
        return package?.ExecutionRuntimeBindings
               ?? throw new RuntimeNotFoundException($"Installed package '{packageId}' was not found.");
    }

    public async Task<RuntimeExecutionRuntimeOperationResult> EnsureAsync(
        RuntimeExecutionRuntimeEnsureRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequirement(request?.Requirement);
        Uri origin;
        try
        {
            origin = RegistryOrigin.Normalize(request!.RegistryOrigin);
        }
        catch (ArgumentException exception)
        {
            throw new RuntimeValidationException(exception.Message);
        }
        if (origin.Scheme != Uri.UriSchemeHttps)
        {
            throw new RuntimeValidationException(
                "Execution runtime acquisition requires an absolute HTTPS Registry origin.");
        }

        var requirement = request.Requirement;
        using var operation = telemetry.BeginOperation();
        try
        {
            RegistryResolveExecutionRuntimeResponse resolution;
            using (var planPhase = telemetry.BeginPhase(ExecutionRuntimePhaseNames.Plan))
            {
                try
                {
                    using var response = await registryClient.SendAsync(
                            new HttpRequestMessage(
                                HttpMethod.Post,
                                new Uri(
                                    origin,
                                    RegistryApiRoutes.ToHttpClientRelativePath(
                                        RegistryApiRoutes.ExecutionRuntimeResolve)))
                            {
                                Content = JsonContent.Create(new RegistryResolveExecutionRuntimeRequest(
                                    new RegistryExecutionRuntimeRequirement(
                                        requirement.RuntimeId,
                                        requirement.VersionRange,
                                        requirement.ModuleAbi),
                                    PackageTargetSelection.GetCurrentRuntimeIdentifier(),
                                    new RegistryHostRuntimeTrustProfile(runtimeTrust.ProfileId, runtimeTrust.Generation),
                                    ExpectedResolutionFingerprint: null)),
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode
                        && response.StatusCode is not (
                            HttpStatusCode.BadRequest
                            or HttpStatusCode.Conflict
                            or HttpStatusCode.Gone))
                    {
                        planPhase.Failed("RegistryResponseFailure");
                        return Failed(
                            RuntimeExecutionRuntimeOperationActions.Ensure,
                            ExecutionRuntimeErrorCodes.DownloadFailed,
                            await registryClient.ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));
                    }

                    resolution = await registryClient
                        .ReadJsonAsync<RegistryResolveExecutionRuntimeResponse>(response, cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidDataException(
                            "Registry returned an empty execution-runtime resolution.");
                    if (!resolution.Success || resolution.RuntimeArtifact is null)
                    {
                        var conflict = resolution.Conflicts?.FirstOrDefault();
                        planPhase.Failed("ResolutionFailure");
                        return Failed(
                            RuntimeExecutionRuntimeOperationActions.Ensure,
                            NormalizeResolutionErrorCode(conflict?.Code),
                            conflict?.Message ?? "No compatible execution runtime could be resolved.");
                    }

                    ValidateResolution(requirement, resolution, timeProvider.GetUtcNow());
                    var identity = resolution.RuntimeArtifact.Identity;
                    planPhase.SetIdentity(new ExecutionRuntimeIdentity(
                        identity.RuntimeId,
                        identity.Version,
                        identity.PackagingRevision,
                        identity.Rid,
                        identity.ModuleAbi));
                    planPhase.Succeeded();
                }
                catch (OperationCanceledException)
                {
                    planPhase.Canceled();
                    throw;
                }
                catch (Exception exception)
                {
                    planPhase.Failed(exception);
                    throw;
                }
            }

            var ensured = await artifactDownloader.DownloadAsync(
                    origin,
                    resolution.RuntimeArtifact,
                    [],
                    cancellationToken)
                .ConfigureAwait(false);
            var (confirmation, policyObservedAtUtc) = await ReconfirmResolutionAsync(
                    origin,
                    requirement,
                    resolution,
                    cancellationToken)
                .ConfigureAwait(false);
            await lifecyclePolicies.ObserveAsync(
                    [new ExecutionRuntimeLifecyclePolicyObservation(
                        ensured.Receipt,
                        origin.AbsoluteUri,
                        confirmation.RuntimeArtifact!.Lifecycle,
                        confirmation.RuntimePolicyRevision,
                        confirmation.RuntimeArtifact.MinimumHostTrustGeneration,
                        policyObservedAtUtc)],
                    cancellationToken)
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
            return MapFailure(RuntimeExecutionRuntimeOperationActions.Ensure, exception, requirement);
        }
    }

    public async Task<RuntimeExecutionRuntimeOperationResult> ImportAsync(
        RuntimeExecutionRuntimeImportRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || !Guid.TryParseExact(request.UploadId, "N", out _))
        {
            throw new RuntimeValidationException("A valid execution-runtime upload id is required.");
        }

        var upload = transfers.AcquireExecutionRuntimeUpload(request.UploadId);
        try
        {
            var imported = await store.ImportLocalArchiveAsync(
                    upload.FilePath,
                    upload.ContentHash,
                    upload.Length,
                    cancellationToken)
                .ConfigureAwait(false);
            var summary = await GetSummaryAsync(imported.Receipt, cancellationToken).ConfigureAwait(false);
            return new RuntimeExecutionRuntimeOperationResult(
                true,
                null,
                imported.AlreadyPresent
                    ? "Execution runtime is already installed and healthy."
                    : "Trusted execution runtime imported. Registry policy is required before a new package can bind it.",
                RuntimeExecutionRuntimeOperationActions.Import,
                summary,
                imported.AlreadyPresent);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return MapFailure(RuntimeExecutionRuntimeOperationActions.Import, exception);
        }
        finally
        {
            transfers.Release(upload);
        }
    }

    public async Task<RuntimeExecutionRuntimeOperationResult> VerifyAsync(
        string runtimeId,
        string version,
        int packagingRevision,
        string rid,
        string moduleAbi,
        CancellationToken cancellationToken)
    {
        var identity = CreateIdentity(runtimeId, version, packagingRevision, rid, moduleAbi);
        var impact = await installedPackages.GetExecutionRuntimeImpactAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        ExecutionRuntimeReceiptV1? receipt;
        try
        {
            receipt = await store.GetReceiptAsync(identity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
        {
            return Failed(
                RuntimeExecutionRuntimeOperationActions.Verify,
                ExecutionRuntimeErrorCodes.IntegrityFailed,
                "The execution runtime receipt failed integrity validation.",
                impact);
        }
        if (receipt is null)
        {
            return Failed(
                RuntimeExecutionRuntimeOperationActions.Verify,
                ExecutionRuntimeErrorCodes.Missing,
                "The exact execution runtime is not installed.",
                impact);
        }

        var verification = await store.VerifyAsync(identity, deep: true, cancellationToken).ConfigureAwait(false);
        var summary = await GetSummaryAsync(receipt, cancellationToken).ConfigureAwait(false);
        return verification.Healthy
            ? new RuntimeExecutionRuntimeOperationResult(
                true,
                null,
                "Execution runtime passed deep verification.",
                RuntimeExecutionRuntimeOperationActions.Verify,
                summary,
                AlreadyPresent: true,
                impact)
            : new RuntimeExecutionRuntimeOperationResult(
                false,
                ExecutionRuntimeErrorCodes.IntegrityFailed,
                "Execution runtime failed deep verification and requires repair.",
                RuntimeExecutionRuntimeOperationActions.Verify,
                summary,
                AlreadyPresent: true,
                impact);
    }

    public async Task<RuntimeExecutionRuntimeOperationResult> RepairAsync(
        string runtimeId,
        string version,
        int packagingRevision,
        string rid,
        string moduleAbi,
        RuntimeExecutionRuntimeActionRequest request,
        CancellationToken cancellationToken)
    {
        var identity = CreateIdentity(runtimeId, version, packagingRevision, rid, moduleAbi);
        if (request is null
            || !PackageContentSignatureValidator.IsLowercaseSha256(request.ExpectedImpactFingerprint))
        {
            throw new RuntimeValidationException(
                "Execution runtime repair requires a valid confirmed impact fingerprint.");
        }

        var preflightImpact = await installedPackages.GetExecutionRuntimeImpactAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        ExecutionRuntimeReceiptV1? receipt;
        var receiptIntegrityFailed = false;
        try
        {
            receipt = await store.GetReceiptAsync(identity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
        {
            receipt = null;
            receiptIntegrityFailed = true;
        }

        if (receipt is null
            || !Directory.Exists(store.Layout.GetContentRootPath(receipt.ContentIdentity)))
        {
            if (preflightImpact.PackageReferenceCount == 0)
            {
                return Failed(
                    RuntimeExecutionRuntimeOperationActions.Repair,
                    receiptIntegrityFailed
                        ? ExecutionRuntimeErrorCodes.IntegrityFailed
                        : ExecutionRuntimeErrorCodes.Missing,
                    receiptIntegrityFailed
                        ? "The execution runtime receipt failed integrity validation and no installed package carries authoritative recovery provenance."
                        : "The exact execution runtime is not installed and no installed package carries authoritative recovery provenance.",
                    preflightImpact);
            }
            return await RecoverMissingExecutionRuntimeAsync(
                    identity,
                    request.ExpectedImpactFingerprint,
                    preflightImpact,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            var repair = await installedPackages.ExecuteExecutionRuntimeRepairAsync(
                    identity,
                    request.ExpectedImpactFingerprint,
                    token => store.VerifyAsync(identity, deep: true, token),
                    token => store.RepairAsync(identity, token),
                    cancellationToken)
                .ConfigureAwait(false);
            if (repair.Verification.Healthy)
            {
                return new RuntimeExecutionRuntimeOperationResult(
                    true,
                    null,
                    "Execution runtime is already healthy.",
                    RuntimeExecutionRuntimeOperationActions.Repair,
                    await GetSummaryAsync(receipt, cancellationToken).ConfigureAwait(false),
                    AlreadyPresent: true,
                    repair.Impact);
            }
            if (repair.Repaired is null)
            {
                return Failed(
                    RuntimeExecutionRuntimeOperationActions.Repair,
                    ExecutionRuntimeErrorCodes.Missing,
                    "The exact execution runtime is not installed.",
                    repair.Impact);
            }
            var reload = repair.Reload
                         ?? throw new InvalidOperationException(
                             "Execution runtime repair completed without package reload evidence.");
            var message = reload.Success
                ? "Execution runtime repaired and dependent packages reloaded."
                : "Execution runtime repaired, but dependent packages could not be reloaded automatically.";
            return new RuntimeExecutionRuntimeOperationResult(
                reload.Success,
                reload.Success ? null : ExecutionRuntimeErrorCodes.InstallFailed,
                message,
                RuntimeExecutionRuntimeOperationActions.Repair,
                await GetSummaryAsync(repair.Repaired.Receipt, cancellationToken).ConfigureAwait(false),
                AlreadyPresent: false,
                repair.Impact);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return MapFailure(
                RuntimeExecutionRuntimeOperationActions.Repair,
                exception,
                impact: preflightImpact,
                identity: identity);
        }
    }

    private async Task<RuntimeExecutionRuntimeOperationResult> RecoverMissingExecutionRuntimeAsync(
        ExecutionRuntimeIdentity identity,
        string expectedImpactFingerprint,
        RuntimeExecutionRuntimeImpact preflightImpact,
        CancellationToken cancellationToken)
    {
        try
        {
            var recovery = await installedPackages.ExecuteExecutionRuntimeRecoveryAsync(
                    identity,
                    expectedImpactFingerprint,
                    token => RestoreExactBoundExecutionRuntimeAsync(identity, token),
                    cancellationToken)
                .ConfigureAwait(false);
            var reload = recovery.Reload;
            return new RuntimeExecutionRuntimeOperationResult(
                reload.Success,
                reload.Success ? null : ExecutionRuntimeErrorCodes.InstallFailed,
                reload.Success
                    ? "The exact bound execution runtime was restored and dependent packages reloaded."
                    : "The exact bound execution runtime was restored, but package reload failed; the previous healthy generation was retained.",
                RuntimeExecutionRuntimeOperationActions.Repair,
                await GetSummaryAsync(recovery.Recovered.Receipt, cancellationToken).ConfigureAwait(false),
                recovery.Recovered.AlreadyPresent,
                recovery.Impact);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return MapFailure(
                RuntimeExecutionRuntimeOperationActions.Repair,
                exception,
                impact: preflightImpact,
                identity: identity);
        }
    }

    private async Task<ExecutionRuntimeEnsureResult> RestoreExactBoundExecutionRuntimeAsync(
        ExecutionRuntimeIdentity identity,
        CancellationToken cancellationToken)
    {
        var authority = await GetExactBindingAuthorityAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        var initial = await GetAuthoritativeExactArtifactAsync(
                authority,
                expectedResolutionFingerprint: null,
                cancellationToken)
            .ConfigureAwait(false);
        var artifact = initial.Resolution.RuntimeArtifact!;
        var binding = authority.Binding;
        var trustEnvironment = runtimeTrust.Classify(
            artifact.SigningKeyId,
            artifact.MinimumHostTrustGeneration);
        var expectedTrustEnvironment = binding.AcquisitionSource
            == RuntimeExecutionRuntimeAcquisitionSources.Development
            ? ExecutionRuntimeTrustEnvironment.Development
            : ExecutionRuntimeTrustEnvironment.Production;
        if (trustEnvironment != expectedTrustEnvironment
            || binding.TrustStoreGeneration != runtimeTrust.Generation)
        {
            throw new RuntimeExecutionRuntimeException(
                ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied,
                "The exact bound execution runtime cannot be recreated under the current Host trust generation without an explicit rebind.");
        }

        var plan = new ExecutionRuntimeAcquisitionPlan(
            identity,
            artifact.ArtifactSha256,
            artifact.ArtifactSize,
            artifact.ManifestSha256,
            initial.ArtifactDetails.ContentIdentity,
            artifact.SigningKeyId,
            artifact.MinimumHostTrustGeneration,
            trustEnvironment,
            binding.AcquisitionSource,
            binding.AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.LocalImport
                ? null
                : authority.Origin.AbsoluteUri,
            artifact.NativeAttestationSha256,
            artifact.NativeAttestationKeyId);

        ExecutionRuntimeEnsureResult ensured;
        try
        {
            ensured = await store.EnsureAsync(plan, archivePath: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException) when (binding.AcquisitionSource
                                             != RuntimeExecutionRuntimeAcquisitionSources.LocalImport)
        {
            ensured = await artifactDownloader.DownloadAsync(
                    authority.Origin,
                    artifact,
                    [],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            throw new RuntimeExecutionRuntimeException(
                ExecutionRuntimeErrorCodes.Missing,
                "The exact locally imported runtime archive is no longer cached. Import that exact trusted artifact again; Registry acquisition was not substituted.");
        }

        if (!RuntimePackageExecutionRuntimeBinding.FromInstalled(binding).MatchesReceipt(ensured.Receipt))
        {
            throw new RuntimeExecutionRuntimeException(
                ExecutionRuntimeErrorCodes.BindingInvalid,
                "The restored execution-runtime receipt does not match the exact committed package binding.");
        }

        var confirmation = await GetAuthoritativeExactArtifactAsync(
                authority,
                initial.Resolution.ResolutionFingerprint,
                cancellationToken)
            .ConfigureAwait(false);
        if (!AuthoritativeArtifactsEquivalent(initial, confirmation))
        {
            throw StalePlan();
        }
        var observedAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
        await lifecyclePolicies.ObserveAsync(
                [new ExecutionRuntimeLifecyclePolicyObservation(
                    ensured.Receipt,
                    authority.Origin.AbsoluteUri,
                    confirmation.Resolution.RuntimeArtifact!.Lifecycle,
                    confirmation.Resolution.RuntimePolicyRevision,
                    confirmation.Resolution.RuntimeArtifact.MinimumHostTrustGeneration,
                    observedAtUtc)],
                cancellationToken)
            .ConfigureAwait(false);
        return ensured;
    }

    private async Task<ExactBindingAuthority> GetExactBindingAuthorityAsync(
        ExecutionRuntimeIdentity identity,
        CancellationToken cancellationToken)
    {
        var bindings = (await installedPackageStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .SelectMany(package => package.ExecutionRuntimeBindings
                .Where(binding => BindingMatches(binding, identity))
                .Select(binding => (package.PackageId, Binding: binding)))
            .OrderBy(static item => item.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (bindings.Length == 0)
        {
            throw new RuntimeExecutionRuntimeException(
                ExecutionRuntimeErrorCodes.Missing,
                "No installed package carries the exact execution-runtime binding required for recovery.");
        }

        var binding = bindings[0].Binding;
        if (bindings.Any(candidate => !RecoveryAuthoritiesEquivalent(binding, candidate.Binding)))
        {
            throw new RuntimeExecutionRuntimeException(
                ExecutionRuntimeErrorCodes.BindingInvalid,
                "Installed packages contain conflicting provenance for the same exact execution runtime.");
        }

        Uri origin;
        try
        {
            origin = RegistryOrigin.Normalize(binding.RegistryOrigin);
        }
        catch (ArgumentException)
        {
            throw new RuntimeExecutionRuntimeException(
                ExecutionRuntimeErrorCodes.BindingInvalid,
                "The exact execution-runtime binding has invalid Registry provenance.");
        }
        return new ExactBindingAuthority(binding, origin);
    }

    private async Task<AuthoritativeExactArtifact> GetAuthoritativeExactArtifactAsync(
        ExactBindingAuthority authority,
        string? expectedResolutionFingerprint,
        CancellationToken cancellationToken)
    {
        var binding = authority.Binding;
        var currentRuntime = new RegistryExecutionRuntimeIdentity(
            binding.RuntimeId,
            binding.RuntimeVersion,
            binding.RuntimePackagingRevision,
            binding.Rid,
            binding.ModuleAbi);
        using var resolveResponse = await registryClient.SendAsync(
                new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri(
                        authority.Origin,
                        RegistryApiRoutes.ToHttpClientRelativePath(
                            RegistryApiRoutes.ExecutionRuntimeResolve)))
                {
                    Content = JsonContent.Create(new RegistryResolveExecutionRuntimeRequest(
                        new RegistryExecutionRuntimeRequirement(
                            binding.RuntimeId,
                            binding.RuntimeVersion,
                            binding.ModuleAbi),
                        binding.Rid,
                        new RegistryHostRuntimeTrustProfile(
                            runtimeTrust.ProfileId,
                            runtimeTrust.Generation),
                        currentRuntime,
                        expectedResolutionFingerprint)),
                },
                cancellationToken)
            .ConfigureAwait(false);
        RegistryResolveExecutionRuntimeResponse resolution;
        try
        {
            resolution = await registryClient
                .ReadJsonAsync<RegistryResolveExecutionRuntimeResponse>(resolveResponse, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "Registry returned an empty exact execution-runtime resolution.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            if (!resolveResponse.IsSuccessStatusCode)
            {
                await registryClient.EnsureSuccessAsync(resolveResponse, cancellationToken).ConfigureAwait(false);
            }
            throw new RuntimeExecutionRuntimeException(
                ExecutionRuntimeErrorCodes.IntegrityFailed,
                "Registry returned corrupt resolution authority for the exact bound execution runtime.");
        }
        if (!resolution.Success || resolution.RuntimeArtifact is null)
        {
            var conflict = resolution.Conflicts?.SingleOrDefault();
            var code = conflict is null
                ? ExecutionRuntimeErrorCodes.Unresolved
                : NormalizeResolutionErrorCode(conflict.Code);
            throw new RuntimeExecutionRuntimeException(code, conflict?.Message);
        }
        if (!resolveResponse.IsSuccessStatusCode)
        {
            await registryClient.EnsureSuccessAsync(resolveResponse, cancellationToken).ConfigureAwait(false);
        }

        var resolvedArtifact = resolution.RuntimeArtifact;
        ValidateRecoveryLifecycle(resolvedArtifact.Lifecycle, timeProvider.GetUtcNow());
        if (!PackageContentSignatureValidator.IsLowercaseSha256(resolution.ResolutionFingerprint)
            || resolution.RuntimePolicyRevision <= 0
            || resolution.ExpiresAtUtc <= timeProvider.GetUtcNow()
            || resolution.Conflicts is null
            || resolution.Conflicts.Count != 0
            || expectedResolutionFingerprint is not null
            && !string.Equals(
                resolution.ResolutionFingerprint,
                expectedResolutionFingerprint,
                StringComparison.Ordinal)
            || !RuntimeIdentitiesEqual(resolvedArtifact.Identity, currentRuntime)
            || !string.Equals(resolvedArtifact.ManifestSha256, binding.RuntimeManifestSha256, StringComparison.Ordinal)
            || !string.Equals(resolvedArtifact.ArtifactSha256, binding.RuntimeArtifactSha256, StringComparison.Ordinal)
            || resolvedArtifact.ArtifactSize != binding.RuntimeArtifactSize
            || !string.Equals(resolvedArtifact.SigningKeyId, binding.SigningKeyId, StringComparison.Ordinal)
            || resolvedArtifact.ObservedRuntimePolicyRevision != resolution.RuntimePolicyRevision)
        {
            throw StalePlan();
        }

        using var response = await registryClient.SendAsync(
                new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri(
                        authority.Origin,
                        RegistryApiRoutes.ToHttpClientRelativePath(
                            RegistryApiRoutes.ExecutionRuntimeRelease(
                                binding.RuntimeId,
                                binding.RuntimeVersion,
                                binding.RuntimePackagingRevision)))),
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new RuntimeExecutionRuntimeException(
                ExecutionRuntimeErrorCodes.Unsupported,
                "Registry no longer exposes the exact bound execution-runtime release.");
        }
        await registryClient.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        RegistryExecutionRuntimeReleaseDetails details;
        try
        {
            details = await registryClient
                .ReadJsonAsync<RegistryExecutionRuntimeReleaseDetails>(response, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "Registry returned an empty exact execution-runtime release.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new RuntimeExecutionRuntimeException(
                ExecutionRuntimeErrorCodes.IntegrityFailed,
                "Registry returned corrupt authority for the exact bound execution runtime.");
        }

        var release = details.Release;
        var matching = details.Artifacts
            .Where(candidate => string.Equals(
                candidate.Artifact.Identity.Rid,
                binding.Rid,
                StringComparison.Ordinal))
            .ToArray();
        if (!string.Equals(release.RuntimeId, binding.RuntimeId, StringComparison.Ordinal)
            || !string.Equals(release.Version, binding.RuntimeVersion, StringComparison.Ordinal)
            || release.PackagingRevision != binding.RuntimePackagingRevision
            || !string.Equals(release.ModuleAbi, binding.ModuleAbi, StringComparison.Ordinal)
            || !string.Equals(release.WorkerProtocol, SunderExecutionRuntimeFormat.WorkerProtocol, StringComparison.Ordinal)
            || matching.Length != 1)
        {
            throw new RuntimeExecutionRuntimeException(
                ExecutionRuntimeErrorCodes.Unsupported,
                "Registry does not expose the exact bound execution-runtime artifact for this device.");
        }

        var artifact = matching[0];
        ValidateRecoveryLifecycle(artifact.Artifact.Lifecycle, timeProvider.GetUtcNow());
        if (!LifecyclesEquivalent(release.Lifecycle, artifact.Artifact.Lifecycle)
            || !ArtifactsEquivalent(artifact.Artifact, resolvedArtifact)
            || release.MinimumHostTrustGeneration != artifact.Artifact.MinimumHostTrustGeneration
            || !PackageContentSignatureValidator.IsLowercaseSha256(artifact.ContentIdentity)
            || artifact.PublishedAtUtc == default
            || release.PublishedAtUtc == default
            || release.SupportedRids is null
            || !release.SupportedRids.Contains(binding.Rid, StringComparer.Ordinal))
        {
            throw new RuntimeExecutionRuntimeException(
                ExecutionRuntimeErrorCodes.IntegrityFailed,
                "Registry authority does not match the immutable exact execution-runtime binding.");
        }
        if (artifact.Artifact.MinimumHostTrustGeneration > runtimeTrust.Generation)
        {
            throw new RuntimeExecutionRuntimeException(
                ExecutionRuntimeErrorCodes.HostTrustUpdateRequired,
                "The Host trust profile must be updated before restoring this exact execution runtime.");
        }
        return new AuthoritativeExactArtifact(details, artifact, resolution);
    }

    private static void ValidateRecoveryLifecycle(
        RegistryExecutionRuntimeLifecycle lifecycle,
        DateTimeOffset now)
    {
        var code = lifecycle.State switch
        {
            ExecutionRuntimeLifecycleStates.Revoked => ExecutionRuntimeErrorCodes.Revoked,
            ExecutionRuntimeLifecycleStates.Retired => ExecutionRuntimeErrorCodes.Retired,
            ExecutionRuntimeLifecycleStates.Deprecated
                when lifecycle.InstallBlockedAtUtc is { } blocked && blocked <= now =>
                ExecutionRuntimeErrorCodes.Deprecated,
            ExecutionRuntimeLifecycleStates.Supported or ExecutionRuntimeLifecycleStates.Deprecated => null,
            _ => ExecutionRuntimeErrorCodes.Unsupported,
        };
        if (code is not null)
        {
            throw new RuntimeExecutionRuntimeException(code, lifecycle.Message);
        }
    }

    private static bool AuthoritativeArtifactsEquivalent(
        AuthoritativeExactArtifact left,
        AuthoritativeExactArtifact right)
        => string.Equals(
               left.Resolution.ResolutionFingerprint,
               right.Resolution.ResolutionFingerprint,
               StringComparison.Ordinal)
           && left.Resolution.RuntimePolicyRevision == right.Resolution.RuntimePolicyRevision
           && ArtifactsEquivalent(left.Resolution.RuntimeArtifact!, right.Resolution.RuntimeArtifact!)
           && string.Equals(left.ArtifactDetails.ContentIdentity, right.ArtifactDetails.ContentIdentity, StringComparison.Ordinal)
           && left.ArtifactDetails.PublishedAtUtc.EqualsExact(right.ArtifactDetails.PublishedAtUtc)
           && ReleaseSummariesEquivalent(left.Details.Release, right.Details.Release);

    private static bool ReleaseSummariesEquivalent(
        RegistryExecutionRuntimeReleaseSummary left,
        RegistryExecutionRuntimeReleaseSummary right)
        => string.Equals(left.RuntimeId, right.RuntimeId, StringComparison.Ordinal)
           && string.Equals(left.Version, right.Version, StringComparison.Ordinal)
           && left.PackagingRevision == right.PackagingRevision
           && string.Equals(left.ModuleAbi, right.ModuleAbi, StringComparison.Ordinal)
           && string.Equals(left.WorkerProtocol, right.WorkerProtocol, StringComparison.Ordinal)
           && left.MinimumHostTrustGeneration == right.MinimumHostTrustGeneration
           && LifecyclesEquivalent(left.Lifecycle, right.Lifecycle)
           && left.SupportedRids.SequenceEqual(right.SupportedRids, StringComparer.Ordinal)
           && left.PublishedAtUtc.EqualsExact(right.PublishedAtUtc);

    private static bool RecoveryAuthoritiesEquivalent(
        InstalledPackageExecutionRuntimeBindingRecord left,
        InstalledPackageExecutionRuntimeBindingRecord right)
        => string.Equals(left.RuntimeManifestSha256, right.RuntimeManifestSha256, StringComparison.Ordinal)
           && string.Equals(left.RuntimeArtifactSha256, right.RuntimeArtifactSha256, StringComparison.Ordinal)
           && left.RuntimeArtifactSize == right.RuntimeArtifactSize
           && string.Equals(left.RegistryOrigin, right.RegistryOrigin, StringComparison.Ordinal)
           && string.Equals(left.AcquisitionSource, right.AcquisitionSource, StringComparison.Ordinal)
           && string.Equals(left.SigningKeyId, right.SigningKeyId, StringComparison.Ordinal)
           && left.TrustStoreGeneration == right.TrustStoreGeneration;

    public async Task<RuntimeExecutionRuntimeOperationResult> CollectGarbageAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var runtimeResult = await store.CollectGarbageAsync(cancellationToken).ConfigureAwait(false);
            var projectionResult = await projectionMaintenance.CollectGarbageAsync(cancellationToken)
                .ConfigureAwait(false);
            var message =
                $"Execution runtime GC removed {runtimeResult.RootsRemoved} roots, {runtimeResult.ArchivesRemoved} archives, and {runtimeResult.ManifestsRemoved} manifests. "
                + $"Package projection GC recovered {projectionResult.RecoveredLeaseCount} stale leases, marked {projectionResult.MarkedEntryCount} entries, removed {projectionResult.TombstonedEntryCount} entries, and preserved {projectionResult.PreservedEntryCount} entries.";
            return new RuntimeExecutionRuntimeOperationResult(
                true,
                null,
                message,
                RuntimeExecutionRuntimeOperationActions.GarbageCollect,
                Runtime: null,
                AlreadyPresent: false);
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
        await lifecyclePolicies.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await BuildSummaryAsync(
                receipt,
                packages,
                lifecyclePolicies.CaptureSnapshot(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<InstalledExecutionRuntimeSummary> BuildSummaryAsync(
        ExecutionRuntimeReceiptV1 receipt,
        IReadOnlyList<InstalledPackageRecord> packages,
        ExecutionRuntimeLifecyclePolicySnapshot policies,
        CancellationToken cancellationToken)
    {
        var verification = await store.VerifyAsync(receipt.Identity, deep: false, cancellationToken)
            .ConfigureAwait(false);
        var hasPolicy = policies.TryGet(receipt.Identity, out var policy)
                        && policy is not null
                        && policy.MatchesReceipt(receipt);
        var now = timeProvider.GetUtcNow();
        var permitsActivation = hasPolicy && policy!.PermitsActivation(now);
        var lifecycle = hasPolicy
            ? ToRuntimeLifecycle(policy!, permitsActivation)
            : new RuntimeExecutionRuntimeLifecycle(
                RuntimeExecutionRuntimeLifecycleStates.PolicyRequired,
                SupportedFromUtc: null,
                DeprecatedAtUtc: null,
                InstallBlockedAtUtc: null,
                ActivationBlockedAtUtc: null,
                RevokedAtUtc: null,
                PolicyMessage: "Trusted Registry lifecycle policy has not been associated with this runtime.",
                RuntimePolicyRevision: 0,
                PolicyObservedAtUtc: receipt.InstalledAtUtc);
        var status = !verification.Healthy
            ? RuntimeExecutionRuntimeStatuses.Unhealthy
            : !permitsActivation
                ? RuntimeExecutionRuntimeStatuses.Degraded
                : RuntimeExecutionRuntimeStatuses.Ready;
        var referenceCount = packages.Count(package =>
            package.ExecutionRuntimeBindings.Any(binding => BindingMatches(binding, receipt)));
        return new InstalledExecutionRuntimeSummary(
            ToRuntimeIdentity(receipt),
            status,
            receipt.Content.Sum(static content => content.Size),
            referenceCount,
            receipt.AcquisitionSource,
            receipt.SigningKeyId,
            receipt.TrustGeneration,
            new RuntimeExecutionRuntimeHealth(
                verification.Healthy
                    ? RuntimeExecutionRuntimeStatuses.Ready
                    : RuntimeExecutionRuntimeStatuses.Unhealthy,
                verification.CheckedAtUtc,
                RepairRecommended: !verification.Healthy,
                verification.Healthy
                    ? null
                    : GetSafeMessage(ExecutionRuntimeErrorCodes.IntegrityFailed)),
            lifecycle,
            receipt.InstalledAtUtc);
    }

    private async Task<(RegistryResolveExecutionRuntimeResponse Resolution, DateTimeOffset ObservedAtUtc)>
        ReconfirmResolutionAsync(
        Uri origin,
        RuntimeExecutionRuntimeRequirement requirement,
        RegistryResolveExecutionRuntimeResponse original,
        CancellationToken cancellationToken)
    {
        using var response = await registryClient.SendAsync(
                new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri(
                        origin,
                        RegistryApiRoutes.ToHttpClientRelativePath(
                            RegistryApiRoutes.ExecutionRuntimeResolve)))
                {
                    Content = JsonContent.Create(new RegistryResolveExecutionRuntimeRequest(
                        new RegistryExecutionRuntimeRequirement(
                            requirement.RuntimeId,
                            requirement.VersionRange,
                            requirement.ModuleAbi),
                        PackageTargetSelection.GetCurrentRuntimeIdentifier(),
                        new RegistryHostRuntimeTrustProfile(runtimeTrust.ProfileId, runtimeTrust.Generation),
                        ExpectedResolutionFingerprint: original.ResolutionFingerprint)),
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest
                or HttpStatusCode.Conflict
                or HttpStatusCode.Gone)
            {
                throw StalePlan();
            }
            await registryClient.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }

        RegistryResolveExecutionRuntimeResponse? confirmation;
        try
        {
            confirmation = await registryClient
                .ReadJsonAsync<RegistryResolveExecutionRuntimeResponse>(response, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw StalePlan();
        }
        if (confirmation is null
            || !confirmation.Success
            || confirmation.RuntimeArtifact is null
            || confirmation.Conflicts is null
            || confirmation.Conflicts.Count != 0)
        {
            throw StalePlan();
        }

        var observedAtUtc = timeProvider.GetUtcNow();
        ValidateResolution(requirement, confirmation, observedAtUtc);
        if (!string.Equals(
                confirmation.ResolutionFingerprint,
                original.ResolutionFingerprint,
                StringComparison.Ordinal)
            || confirmation.RuntimePolicyRevision != original.RuntimePolicyRevision
            || !ArtifactsEquivalent(original.RuntimeArtifact!, confirmation.RuntimeArtifact))
        {
            throw StalePlan();
        }
        return (confirmation, observedAtUtc);
    }

    private static void ValidateRequirement(RuntimeExecutionRuntimeRequirement? requirement)
    {
        var validPair = requirement?.RuntimeId switch
        {
            SunderExecutionRuntimeFormat.DotNetRuntimeId =>
                requirement.ModuleAbi == SunderExecutionRuntimeFormat.DotNetModuleAbi,
            SunderExecutionRuntimeFormat.NodeRuntimeId =>
                requirement.ModuleAbi == SunderExecutionRuntimeFormat.NodeModuleAbi,
            _ => false,
        };
        if (!validPair
            || !PackageTargetValidator.IsBoundedStableMajorRange(requirement!.VersionRange))
        {
            throw new RuntimeExecutionRuntimeException(
                ExecutionRuntimeErrorCodes.RequirementInvalid,
                "The execution runtime requirement must use a supported runtime id, module ABI, and strict version range.");
        }
    }

    private void ValidateResolution(
        RuntimeExecutionRuntimeRequirement requirement,
        RegistryResolveExecutionRuntimeResponse resolution,
        DateTimeOffset observedAtUtc)
    {
        var artifact = resolution.RuntimeArtifact!;
        if (string.IsNullOrWhiteSpace(resolution.ResolutionFingerprint)
            || resolution.RuntimePolicyRevision <= 0
            || resolution.ExpiresAtUtc <= observedAtUtc
            || resolution.Conflicts is null
            || resolution.Conflicts.Count != 0
            || artifact.Identity is null
            || artifact.Lifecycle is null
            || artifact.ObservedRuntimePolicyRevision != resolution.RuntimePolicyRevision
            || !string.Equals(artifact.Identity.RuntimeId, requirement.RuntimeId, StringComparison.Ordinal)
            || !string.Equals(artifact.Identity.ModuleAbi, requirement.ModuleAbi, StringComparison.Ordinal)
            || !PackageVersionRange.IsSatisfiedBy(artifact.Identity.Version, requirement.VersionRange))
        {
            throw new ExecutionRuntimePlanMismatchException(
                "Registry execution-runtime resolution does not match the requested requirement or current policy authority.");
        }
    }

    private static bool ArtifactsEquivalent(
        RegistryResolvedExecutionRuntimeArtifact left,
        RegistryResolvedExecutionRuntimeArtifact right)
        => RuntimeIdentitiesEqual(left.Identity, right.Identity)
           && string.Equals(left.ManifestSha256, right.ManifestSha256, StringComparison.Ordinal)
           && string.Equals(left.ArtifactSha256, right.ArtifactSha256, StringComparison.Ordinal)
           && left.ArtifactSize == right.ArtifactSize
           && string.Equals(left.SigningKeyId, right.SigningKeyId, StringComparison.Ordinal)
           && left.MinimumHostTrustGeneration == right.MinimumHostTrustGeneration
           && string.Equals(
               left.NativeAttestationSha256,
               right.NativeAttestationSha256,
               StringComparison.Ordinal)
           && string.Equals(
               left.NativeAttestationKeyId,
               right.NativeAttestationKeyId,
               StringComparison.Ordinal)
           && LifecyclesEquivalent(left.Lifecycle, right.Lifecycle)
           && left.ObservedRuntimePolicyRevision == right.ObservedRuntimePolicyRevision;

    private static bool RuntimeIdentitiesEqual(
        RegistryExecutionRuntimeIdentity left,
        RegistryExecutionRuntimeIdentity right)
        => left is not null
           && right is not null
           && string.Equals(left.RuntimeId, right.RuntimeId, StringComparison.Ordinal)
           && string.Equals(left.Version, right.Version, StringComparison.Ordinal)
           && left.PackagingRevision == right.PackagingRevision
           && string.Equals(left.Rid, right.Rid, StringComparison.Ordinal)
           && string.Equals(left.ModuleAbi, right.ModuleAbi, StringComparison.Ordinal);

    private static bool LifecyclesEquivalent(
        RegistryExecutionRuntimeLifecycle left,
        RegistryExecutionRuntimeLifecycle right)
        => left is not null
           && right is not null
           && string.Equals(left.State, right.State, StringComparison.Ordinal)
           && left.StagedAtUtc.EqualsExact(right.StagedAtUtc)
           && DateTimesEqualExact(left.SupportedFromUtc, right.SupportedFromUtc)
           && DateTimesEqualExact(left.DeprecatedAtUtc, right.DeprecatedAtUtc)
           && DateTimesEqualExact(left.InstallBlockedAtUtc, right.InstallBlockedAtUtc)
           && DateTimesEqualExact(left.ActivationBlockedAtUtc, right.ActivationBlockedAtUtc)
           && DateTimesEqualExact(left.RevokedAtUtc, right.RevokedAtUtc)
           && string.Equals(left.Message, right.Message, StringComparison.Ordinal);

    private static bool DateTimesEqualExact(DateTimeOffset? left, DateTimeOffset? right)
        => left.HasValue == right.HasValue
           && (!left.HasValue || left.Value.EqualsExact(right!.Value));

    private static ExecutionRuntimePlanMismatchException StalePlan()
        => new("Registry execution-runtime resolution authority could not be reconfirmed.");

    private static ExecutionRuntimeIdentity CreateIdentity(
        string runtimeId,
        string version,
        int packagingRevision,
        string rid,
        string moduleAbi)
    {
        try
        {
            ExecutionRuntimeStoreLayout.ValidateSegment(runtimeId, nameof(runtimeId));
            ExecutionRuntimeStoreLayout.ValidateSegment(version, nameof(version));
            ExecutionRuntimeStoreLayout.ValidateSegment(rid, nameof(rid));
            ExecutionRuntimeStoreLayout.ValidateSegment(moduleAbi, nameof(moduleAbi));
            if (packagingRevision is <= 0 or > SunderExecutionRuntimeFormat.MaxPackagingRevision)
            {
                throw new ArgumentOutOfRangeException(nameof(packagingRevision));
            }
            return new ExecutionRuntimeIdentity(runtimeId, version, packagingRevision, rid, moduleAbi);
        }
        catch (ArgumentException exception)
        {
            throw new RuntimeValidationException(exception.Message);
        }
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
            ExecutionRuntimeRootInUseException => ExecutionRuntimeErrorCodes.Busy,
            RuntimeWorkerCleanupUnprovenException => ExecutionRuntimeErrorCodes.Busy,
            HttpRequestException { StatusCode: null } or TimeoutException
                when action == RuntimeExecutionRuntimeOperationActions.Repair && identity is not null =>
                ExecutionRuntimeErrorCodes.Offline,
            HttpRequestException or TimeoutException => ExecutionRuntimeErrorCodes.DownloadFailed,
            ExecutionRuntimeTrustException or CryptographicException => ExecutionRuntimeErrorCodes.ArtifactInvalid,
            ExecutionRuntimePlanMismatchException => ExecutionRuntimeErrorCodes.PlanStale,
            InvalidDataException when action == RuntimeExecutionRuntimeOperationActions.Repair =>
                ExecutionRuntimeErrorCodes.IntegrityFailed,
            InvalidDataException => ExecutionRuntimeErrorCodes.ArtifactInvalid,
            ExecutionRuntimeStoreConflictException => ExecutionRuntimeErrorCodes.InstallFailed,
            IOException or UnauthorizedAccessException => ExecutionRuntimeErrorCodes.InstallFailed,
            _ => ExecutionRuntimeErrorCodes.InstallFailed,
        };
        logger.LogWarning(
            exception,
            "Execution runtime action {RuntimeAction} failed with {ErrorCode} ({ExceptionType})",
            action,
            code,
            exception.GetType().Name);
        var message = exception is RuntimeExecutionRuntimeException
            ? exception.Message
            : code == ExecutionRuntimeErrorCodes.DownloadFailed && requirement is not null
                ? $"Registry could not be reached while ensuring '{requirement.RuntimeId}' range '{requirement.VersionRange}' for RID '{PackageTargetSelection.GetCurrentRuntimeIdentifier()}'. The device may be offline; retry online or run 'sunder runtime import --file <artifact.sunderruntime>'."
                : GetSafeMessage(code);
        return Failed(action, code, message, impact);
    }

    private static string GetSafeMessage(string code)
        => ExecutionRuntimeErrorCatalog.All.Single(candidate => candidate.Code == code).SafeMessage;

    private static RuntimeExecutionRuntimeOperationResult Failed(
        string action,
        string code,
        string message,
        RuntimeExecutionRuntimeImpact? impact = null)
        => new(false, code, message, action, Runtime: null, AlreadyPresent: false, impact);

    private static string NormalizeResolutionErrorCode(string? code)
    {
        if (ExecutionRuntimeErrorCatalog.All.Any(descriptor => descriptor.Code == code))
        {
            return code!;
        }
        return code is ExecutionRuntimeResolutionConflictCodes.NoCompatibleVersion
            or ExecutionRuntimeResolutionConflictCodes.RidUnavailable
            or ExecutionRuntimeResolutionConflictCodes.ModuleAbiMismatch
            ? code
            : ExecutionRuntimeErrorCodes.Unresolved;
    }

    private static RuntimeExecutionRuntimeIdentity ToRuntimeIdentity(ExecutionRuntimeReceiptV1 receipt)
        => new(
            receipt.Identity.RuntimeId,
            receipt.Identity.Version,
            receipt.Identity.PackagingRevision,
            receipt.Identity.Rid,
            receipt.Identity.ModuleAbi,
            receipt.ManifestSha256,
            receipt.ArtifactSha256);

    private static RuntimeExecutionRuntimeLifecycle ToRuntimeLifecycle(
        ExecutionRuntimeLifecyclePolicyEntryV1 policy,
        bool permitsActivation)
        => new(
            policy.Lifecycle.State,
            policy.Lifecycle.SupportedFromUtc,
            policy.Lifecycle.DeprecatedAtUtc,
            policy.Lifecycle.InstallBlockedAtUtc,
            policy.Lifecycle.ActivationBlockedAtUtc,
            policy.Lifecycle.RevokedAtUtc,
            GetLifecycleMessage(policy, permitsActivation),
            policy.RuntimePolicyRevision,
            policy.PolicyObservedAtUtc);

    private static string? GetLifecycleMessage(
        ExecutionRuntimeLifecyclePolicyEntryV1 policy,
        bool permitsActivation)
    {
        if (!permitsActivation
            && policy.TrustDisposition == ExecutionRuntimeTrustDispositions.Denied)
        {
            return "Registry policy denies activation of this execution runtime. Update dependent packages to a trusted runtime.";
        }
        if (permitsActivation || !string.IsNullOrWhiteSpace(policy.Lifecycle.Message))
        {
            return policy.Lifecycle.Message;
        }
        return "Registry policy no longer permits activation of this execution runtime. Update dependent packages to a supported runtime.";
    }

    private static bool BindingMatches(
        InstalledPackageExecutionRuntimeBindingRecord binding,
        ExecutionRuntimeIdentity identity)
        => string.Equals(binding.RuntimeId, identity.RuntimeId, StringComparison.Ordinal)
           && string.Equals(binding.RuntimeVersion, identity.Version, StringComparison.Ordinal)
           && binding.RuntimePackagingRevision == identity.PackagingRevision
           && string.Equals(binding.Rid, identity.Rid, StringComparison.Ordinal)
           && string.Equals(binding.ModuleAbi, identity.ModuleAbi, StringComparison.Ordinal);

    private static bool BindingMatches(
        InstalledPackageExecutionRuntimeBindingRecord binding,
        ExecutionRuntimeReceiptV1 receipt)
        => BindingMatches(binding, receipt.Identity)
           && string.Equals(binding.RuntimeManifestSha256, receipt.ManifestSha256, StringComparison.Ordinal)
           && string.Equals(binding.RuntimeArtifactSha256, receipt.ArtifactSha256, StringComparison.Ordinal);

    private sealed record ExactBindingAuthority(
        InstalledPackageExecutionRuntimeBindingRecord Binding,
        Uri Origin);

    private sealed record AuthoritativeExactArtifact(
        RegistryExecutionRuntimeReleaseDetails Details,
        RegistryExecutionRuntimeArtifactDetails ArtifactDetails,
        RegistryResolveExecutionRuntimeResponse Resolution);
}
