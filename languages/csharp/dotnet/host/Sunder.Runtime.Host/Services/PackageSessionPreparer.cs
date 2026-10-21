using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Package.Projections;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionPreparer
{
    private static readonly JsonSerializerOptions InstalledMetadataJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly string _runtimeIdentifier;
    private readonly ILogger _logger;
    private readonly IEphemeralExecutionRuntimeBindingResolver? _ephemeralRuntimeBindingResolver;
    private readonly PackageProjectionCache _projectionCache;
    private readonly Guid _hostInstanceId;
    private readonly DevelopmentImportAuthorityStore? _developmentImports;

    public PackageSessionPreparer(
        string? runtimeIdentifier = null,
        ILogger? logger = null,
        IEphemeralExecutionRuntimeBindingResolver? ephemeralRuntimeBindingResolver = null,
        PackageProjectionCache? projectionCache = null,
        Guid? hostInstanceId = null,
        DevelopmentImportAuthorityStore? developmentImports = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _ephemeralRuntimeBindingResolver = ephemeralRuntimeBindingResolver;
        _projectionCache = projectionCache ?? new PackageProjectionCache();
        _developmentImports = developmentImports;
        _hostInstanceId = hostInstanceId is { } instanceId && instanceId != Guid.Empty
            ? instanceId
            : Guid.NewGuid();
        _runtimeIdentifier = runtimeIdentifier ?? PackageTargetSelection.GetCurrentRuntimeIdentifier();
        if (!SunderPackageFormat.IsRuntimeIdentifier(_runtimeIdentifier))
        {
            throw new ArgumentException(
                $"Runtime package target selection requires one of the six supported exact RIDs; '{_runtimeIdentifier}' is unsupported.",
                nameof(runtimeIdentifier));
        }
    }

    public async Task<PreparedRuntimePackage?> PrepareDevPackageAsync(
        int index,
        string folder,
        string sessionFolder,
        ICollection<string> errors,
        CancellationToken cancellationToken,
        PackageSessionStartupTracePackage? startupTrace = null)
    {
        var preparationStarted = Stopwatch.GetTimestamp();
        if (!Directory.Exists(folder))
        {
            errors.Add($"Dev package folder '{folder}' does not exist.");
            _logger.LogWarning(
                "Dev package validation failed in {ElapsedMilliseconds} ms",
                Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            return null;
        }

        var validationStarted = Stopwatch.GetTimestamp();
        var validation = await ValidateSourceAsync(
            folder,
            "Dev package",
            errors,
            cancellationToken,
            startupTrace);
        if (validation is null)
        {
            _logger.LogWarning(
                "Dev package validation failed in {ElapsedMilliseconds} ms",
                Stopwatch.GetElapsedTime(validationStarted).TotalMilliseconds);
            return null;
        }
        _logger.LogInformation(
            "Validated {PackageSourceKind} package {PackageId} in {ElapsedMilliseconds} ms",
            PackageSourceKind.Dev,
            validation.Manifest.Id,
            Stopwatch.GetElapsedTime(validationStarted).TotalMilliseconds);
        using var preparationScope = _logger.BeginScope(
            "Preparing {PackageSourceKind} package {PackageId}",
            PackageSourceKind.Dev,
            validation.Manifest.Id);

        var packageFolderName = $"{index:D2}-{SanitizeFolderName(validation.Manifest.Id)}";
        var canonicalShadow = Path.Combine(sessionFolder, packageFolderName, "source");
        var canonicalShadowStarted = Stopwatch.GetTimestamp();
        var canonicalShadowMaterialized = false;
        try
        {
            await PackageProjectionMaterializer.MaterializeCanonicalPackageAsync(
                folder,
                canonicalShadow,
                validation.ContentIndex,
                cancellationToken,
                startupTrace);
            canonicalShadowMaterialized = true;
            _logger.LogInformation(
                "Materialized canonical shadow for package {PackageId} in {ElapsedMilliseconds} ms",
                validation.Manifest.Id,
                Stopwatch.GetElapsedTime(canonicalShadowStarted).TotalMilliseconds);
            var contentIdentity = ComputeContentIdentity(canonicalShadow, startupTrace);
            DevelopmentImportAuthority? imports = null;
            if (PackageCompatibilityAuthority.NeedsEvidence(validation.Manifest))
                imports = _developmentImports is null ? throw DevelopmentImportAuthorityStore.Required()
                    : await _developmentImports.ResolveAsync(new(validation.Manifest.Id!, PackageSourceKind.Dev, folder, canonicalShadow,
                        ContentIdentity: contentIdentity, Manifest: validation.Manifest, ContentIndex: validation.ContentIndex), cancellationToken);
            var prepared = await PrepareValidatedPackageAsync(
                folder,
                canonicalShadow,
                canonicalShadow,
                Path.Combine(sessionFolder, packageFolderName, "runtime"),
                PackageSourceKind.Dev,
                validation.Manifest,
                validation.ContentIndex,
                contentIdentity,
                approvedSensitiveCapabilities: [],
                installedRuntimeBindings: null,
                errors,
                cancellationToken,
                startupTrace);
            if (prepared is null)
            {
                _logger.LogWarning(
                    "Preparation failed for {PackageSourceKind} package {PackageId} after {ElapsedMilliseconds} ms",
                    PackageSourceKind.Dev,
                    validation.Manifest.Id,
                    Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            }
            else
            {
                prepared = prepared with { Source = prepared.Source with
                {
                    DevelopmentImports = imports,
                    DevelopmentWorkerContentIdentity = DevelopmentWorkerContentIdentity.Create(validation.ContentIndex),
                } };
                _logger.LogInformation(
                    "Prepared {PackageSourceKind} package {PackageId} in {ElapsedMilliseconds} ms",
                    PackageSourceKind.Dev,
                    validation.Manifest.Id,
                    Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            }
            return prepared;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Canceled preparation for {PackageSourceKind} package {PackageId} after {ElapsedMilliseconds} ms",
                PackageSourceKind.Dev,
                validation.Manifest.Id,
                Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            if (!canonicalShadowMaterialized)
            {
                _logger.LogWarning(
                    "Canonical shadow materialization failed for package {PackageId} after {ElapsedMilliseconds} ms",
                    validation.Manifest.Id,
                    Stopwatch.GetElapsedTime(canonicalShadowStarted).TotalMilliseconds);
            }
            _logger.LogWarning(
                "Preparation failed for {PackageSourceKind} package {PackageId} after {ElapsedMilliseconds} ms",
                PackageSourceKind.Dev,
                validation.Manifest.Id,
                Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            errors.Add($"Failed to materialize dev package '{validation.Manifest.Id}': {exception.Message}");
            return null;
        }
    }

    public async Task<PreparedRuntimePackage?> PrepareInstalledPackageAsync(
        int index,
        InstalledPackageRecord package,
        string physicalSourceRoot,
        string sessionFolder,
        ICollection<string> errors,
        CancellationToken cancellationToken,
        PackageSessionStartupTracePackage? startupTrace = null,
        InstalledPackageValidation? validatedPackage = null)
    {
        var preparationStarted = Stopwatch.GetTimestamp();
        using var preparationScope = _logger.BeginScope(
            "Preparing {PackageSourceKind} package {PackageId}",
            PackageSourceKind.Installed,
            package.PackageId);
        try
        {
            var validation = validatedPackage ?? await ValidateInstalledPackageAsync(
                    package,
                    physicalSourceRoot,
                    errors,
                    cancellationToken,
                    startupTrace)
                .ConfigureAwait(false);
            if (validation is null)
            {
                _logger.LogWarning(
                    "Preparation failed for {PackageSourceKind} package {PackageId} after {ElapsedMilliseconds} ms",
                    PackageSourceKind.Installed,
                    package.PackageId,
                    Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
                return null;
            }

            var packageFolderName = $"{index:D2}-{SanitizeFolderName(package.PackageId)}";
            var prepared = await PrepareValidatedPackageAsync(
                package.InstallPath,
                PathsEqual(package.InstallPath, physicalSourceRoot) ? null : physicalSourceRoot,
                physicalSourceRoot,
                Path.Combine(sessionFolder, packageFolderName, "runtime"),
                PackageSourceKind.Installed,
                validation.Manifest,
                validation.ContentIndex,
                package.ContentIdentity,
                package.CapabilityGrants.Select(static grant => grant.Capability).ToArray(),
                package.ExecutionRuntimeBindings,
                errors,
                cancellationToken,
                startupTrace,
                InstalledPackageProjection.Keys(package));
            if (prepared is null)
            {
                _logger.LogWarning(
                    "Preparation failed for {PackageSourceKind} package {PackageId} after {ElapsedMilliseconds} ms",
                    PackageSourceKind.Installed,
                    package.PackageId,
                    Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            }
            else
            {
                var source = PackagePolicyAdmission.WithProvenance(prepared.Source, package);
                prepared = prepared with { Source = source, Dependencies = source.PackageDependencies };
                _logger.LogInformation(
                    "Prepared {PackageSourceKind} package {PackageId} in {ElapsedMilliseconds} ms",
                    PackageSourceKind.Installed,
                    package.PackageId,
                    Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            }
            return prepared;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Canceled preparation for {PackageSourceKind} package {PackageId} after {ElapsedMilliseconds} ms",
                PackageSourceKind.Installed,
                package.PackageId,
                Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            throw;
        }
        catch
        {
            _logger.LogWarning(
                "Preparation failed for {PackageSourceKind} package {PackageId} after {ElapsedMilliseconds} ms",
                PackageSourceKind.Installed,
                package.PackageId,
                Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            throw;
        }
    }

    public async Task<RuntimePackageActivationState?> ReadInstalledActivationStateAsync(
        InstalledPackageRecord package,
        string physicalSourceRoot,
        ICollection<string> errors,
        CancellationToken cancellationToken,
        PackageSessionStartupTracePackage? startupTrace = null)
    {
        var validation = await ValidateInstalledPackageAsync(
                package,
                physicalSourceRoot,
                errors,
                cancellationToken,
                startupTrace)
            .ConfigureAwait(false);
        if (validation is null)
        {
            return null;
        }
        return validation.Activation;
    }

    internal async Task<InstalledPackageValidation?> ValidateInstalledPackageAsync(
        InstalledPackageRecord package,
        string physicalSourceRoot,
        ICollection<string> errors,
        CancellationToken cancellationToken,
        PackageSessionStartupTracePackage? startupTrace = null)
    {
        var validation = await ValidateInstalledSourceAsync(
                package,
                physicalSourceRoot,
                errors,
                cancellationToken,
                startupTrace)
            .ConfigureAwait(false);
        return validation is null
            ? null
            : new InstalledPackageValidation(
                validation.Manifest,
                validation.ContentIndex,
                CreateActivationState(validation.Manifest, selectedTargetKey: null, selectedTarget: null, InstalledPackageProjection.Keys(package)));
    }

    private async Task<PreparedRuntimePackage?> PrepareValidatedPackageAsync(
        string sourceFolder,
        string? snapshotFolder,
        string physicalSourceRoot,
        string projectionFolder,
        PackageSourceKind sourceKind,
        SunderPackageManifest manifest,
        SunderPackageContentIndex contentIndex,
        string contentIdentity,
        IReadOnlyList<string> approvedSensitiveCapabilities,
        IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>? installedRuntimeBindings,
        ICollection<string> errors,
        CancellationToken cancellationToken,
        PackageSessionStartupTracePackage? startupTrace,
        IReadOnlyList<SunderPackageTargetKey>? acquiredTargets = null)
    {
        PreparedPackageProjectionLease? projectionLease = null;
        ResolvedEphemeralExecutionRuntimeBinding? ephemeralBinding = null;
        var retainResources = false;
        try
        {
            PackageManifestAdmissionPolicy.EnsureSupported(manifest);
            InstalledPackageProjection.ValidateScope(acquiredTargets, manifest);
            var hostRoles = PackageTargetSelection.GetHostRoles(manifest, acquiredTargets);
            var dependencies = (manifest.DependsOn ?? [])
                .Select(static dependency => new PackageDependencyDescriptor(
                    dependency.PackageId!,
                    dependency.VersionRange!))
                .ToArray();
            SunderPackageTargetKey? selectedTargetKey = null;
            SunderPackageTargetManifest? selectedTarget = null;
            string? entryAssemblyPath = null;
            RuntimePackageExecutionRuntimeBinding? executionRuntimeBinding = null;
            IReadOnlyDictionary<string, SunderRpcContractDescriptor> rpcContracts =
                new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal);
            var effectiveProjectionFolder = projectionFolder;

            if ((hostRoles & PackageHostRoles.Runtime) != 0)
            {
                var runtimeTargetKey = new SunderPackageTargetKey(
                    SunderPackageFormat.RuntimeHostRole,
                    _runtimeIdentifier);
                if (!InstalledPackageProjection.Includes(acquiredTargets, runtimeTargetKey.Role, runtimeTargetKey.Rid)
                    || !SunderPackageTargetResolver.TryResolveTarget(manifest, runtimeTargetKey, out var runtimeTarget))
                {
                    errors.Add(
                        $"Package '{manifest.Id}' declares Runtime targets but does not declare the exact current Runtime target '{runtimeTargetKey}'. RID fallback is not supported.");
                    return null;
                }
                var exactRuntimeTarget = runtimeTarget
                    ?? throw new InvalidDataException($"Package target resolver returned no metadata for '{runtimeTargetKey}'.");
                if (!string.Equals(
                        exactRuntimeTarget.Kind,
                        SunderPackageFormat.WorkerTargetKind,
                        StringComparison.Ordinal))
                {
                    errors.Add(
                        $"Package '{manifest.Id}' exact Runtime target '{runtimeTargetKey}' has unsupported target kind '{exactRuntimeTarget.Kind}'. The Runtime Host requires '{SunderPackageFormat.WorkerTargetKind}'.");
                    return null;
                }

                var compatibilityErrors = SunderSdkCompatibilityProfile.Validate(manifest.Id, runtimeTargetKey, exactRuntimeTarget);
                if (compatibilityErrors.Count > 0)
                {
                    foreach (var error in compatibilityErrors) errors.Add(error);
                    return null;
                }

                if (sourceKind == PackageSourceKind.Installed)
                {
                    if ((installedRuntimeBindings is null || installedRuntimeBindings.Count == 0)
                        && _ephemeralRuntimeBindingResolver is ITestOnlyInstalledExecutionRuntimeBindingResolver testResolver)
                    {
                        var testBinding = await testResolver.ResolveEphemeralBindingAsync(
                                runtimeTargetKey,
                                exactRuntimeTarget,
                                cancellationToken)
                            .ConfigureAwait(false);
                        executionRuntimeBinding = ValidateEphemeralRuntimeBinding(
                            manifest.Id!,
                            runtimeTargetKey,
                            exactRuntimeTarget,
                            testBinding.Binding,
                            errors);
                        await testBinding.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        executionRuntimeBinding = SelectInstalledRuntimeBinding(
                            manifest.Id!,
                            runtimeTargetKey,
                            exactRuntimeTarget,
                            installedRuntimeBindings,
                            errors);
                    }
                    if (executionRuntimeBinding is null)
                    {
                        return null;
                    }
                }
                else if (_ephemeralRuntimeBindingResolver is null)
                {
                    errors.Add(
                        $"Development package '{manifest.Id}' cannot resolve an exact execution-runtime binding because local binding authority is unavailable.");
                    return null;
                }

                var projectionStarted = Stopwatch.GetTimestamp();
                try
                {
                    if (sourceKind == PackageSourceKind.Installed)
                    {
                        startupTrace?.BeginProjectionCache();
                        var request = PackageProjectionRequestFactory.Create(
                            physicalSourceRoot,
                            manifest.Id!,
                            contentIdentity,
                            manifest,
                            contentIndex,
                            runtimeTargetKey,
                            exactRuntimeTarget,
                            PackageProjectionRole.Runtime,
                            includeManifest: false);
                        var acquisition = await _projectionCache.GetOrBuildAndLeaseAsync(
                                request,
                                new PackageProjectionOwner(
                                    "runtime-host",
                                    _hostInstanceId,
                                    manifest.Id!,
                                    GetSessionId(projectionFolder)),
                                cancellationToken)
                            .ConfigureAwait(false);
                        effectiveProjectionFolder = acquisition.RootPath;
                        projectionLease = new PreparedPackageProjectionLease(
                            _projectionCache,
                            acquisition,
                            _hostInstanceId,
                            manifest.Id!,
                            request.WarmValidationPaths ?? []);
                        startupTrace?.AddIo(
                            acquisition.Metrics.BytesRead,
                            acquisition.Metrics.BytesHashed,
                            acquisition.Metrics.BytesCopied);
                        startupTrace?.CompleteProjectionCache(ToCacheResult(acquisition.Outcome));
                        _logger.LogInformation(
                            "Runtime projection cache {CacheResult} for package {PackageId} in {ElapsedMilliseconds} ms; read {BytesRead} bytes, hashed {BytesHashed} bytes, copied {BytesCopied} bytes",
                            ToCacheResult(acquisition.Outcome),
                            manifest.Id,
                            Stopwatch.GetElapsedTime(projectionStarted).TotalMilliseconds,
                            acquisition.Metrics.BytesRead,
                            acquisition.Metrics.BytesHashed,
                            acquisition.Metrics.BytesCopied);
                    }
                    else
                    {
                        await PackageProjectionMaterializer.MaterializeProjectionAsync(
                            physicalSourceRoot,
                            projectionFolder,
                            manifest,
                            contentIndex,
                            runtimeTargetKey,
                            cancellationToken,
                            startupTrace);
                        _logger.LogInformation(
                            "Materialized development Runtime target projection for package {PackageId} in {ElapsedMilliseconds} ms",
                            manifest.Id,
                            Stopwatch.GetElapsedTime(projectionStarted).TotalMilliseconds);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (sourceKind == PackageSourceKind.Installed)
                    {
                        startupTrace?.CompleteProjectionCache("miss", "canceled");
                    }
                    throw;
                }
                catch (Exception exception)
                {
                    if (sourceKind == PackageSourceKind.Installed)
                    {
                        startupTrace?.CompleteProjectionCache("miss", "failed");
                    }
                    _logger.LogWarning(
                        "Runtime target projection failed for package {PackageId} after {ElapsedMilliseconds} ms",
                        manifest.Id,
                        Stopwatch.GetElapsedTime(projectionStarted).TotalMilliseconds);
                    errors.Add(
                        $"Package '{manifest.Id}' target '{runtimeTargetKey}' projection failed: {exception.Message}");
                    return null;
                }

                selectedTargetKey = runtimeTargetKey;
                selectedTarget = exactRuntimeTarget;
                var entryPoint = ArchiveRelativePath.Parse(
                    exactRuntimeTarget.EntryPoint!,
                    SunderPackageFormat.MaxLogicalPathLength,
                    SunderPackageFormat.MaxLogicalPathDepth);
                entryAssemblyPath = entryPoint.ToPlatformPath(effectiveProjectionFolder);
                if (!File.Exists(entryAssemblyPath))
                {
                    errors.Add(
                        $"Package '{manifest.Id}' target '{runtimeTargetKey}' declared entry point '{exactRuntimeTarget.EntryPoint}' that was not materialized.");
                    return null;
                }
                var contractLoadStarted = Stopwatch.GetTimestamp();
                try
                {
                    rpcContracts = await LoadRpcContractsAsync(
                        effectiveProjectionFolder,
                        manifest,
                        cancellationToken,
                        startupTrace);
                    _logger.LogInformation(
                        "Loaded {ContractCount} RPC contract(s) for package {PackageId} in {ElapsedMilliseconds} ms",
                        rpcContracts.Count,
                        manifest.Id,
                        Stopwatch.GetElapsedTime(contractLoadStarted).TotalMilliseconds);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    _logger.LogWarning(
                        "RPC contract loading failed for package {PackageId} after {ElapsedMilliseconds} ms",
                        manifest.Id,
                        Stopwatch.GetElapsedTime(contractLoadStarted).TotalMilliseconds);
                    throw;
                }
            }
            else
            {
                Directory.CreateDirectory(projectionFolder);
            }

            if (sourceKind == PackageSourceKind.Dev && selectedTargetKey is { } devTargetKey)
            {
                ephemeralBinding = await _ephemeralRuntimeBindingResolver!.ResolveEphemeralBindingAsync(
                        devTargetKey,
                        selectedTarget!,
                        cancellationToken)
                    .ConfigureAwait(false);
                executionRuntimeBinding = ValidateEphemeralRuntimeBinding(
                    manifest.Id!,
                    devTargetKey,
                    selectedTarget!,
                    ephemeralBinding.Binding,
                    errors);
                if (executionRuntimeBinding is null)
                {
                    return null;
                }
            }

            var preparedSource = new RuntimePackageSource(
                manifest.Id!,
                sourceKind,
                sourceFolder,
                snapshotFolder,
                hostRoles,
                dependencies,
                contentIdentity,
                manifest,
                contentIndex,
                selectedTargetKey,
                selectedTarget) { AcquiredTargets = acquiredTargets };
            var prepared = new PreparedRuntimePackage(
                sourceFolder,
                preparedSource,
                effectiveProjectionFolder,
                manifest.Id!,
                manifest.Version!,
                hostRoles,
                CreateActivationState(manifest, selectedTargetKey, selectedTarget, acquiredTargets),
                entryAssemblyPath,
                selectedTargetKey,
                selectedTarget,
                dependencies,
                rpcContracts,
                GetManifestSha256(contentIndex))
            {
                ExecutionRuntimeBinding = executionRuntimeBinding,
                SessionId = GetSessionId(projectionFolder),
                ApprovedSensitiveCapabilities = approvedSensitiveCapabilities,
                ExecutionRuntimeRetention = ephemeralBinding,
                PackageProjectionLease = projectionLease,
                StartupTrace = startupTrace,
            };
            retainResources = true;
            return prepared;
        }
        finally
        {
            if (!retainResources)
            {
                if (ephemeralBinding is not null)
                {
                    await ephemeralBinding.DisposeAsync().ConfigureAwait(false);
                }
                if (projectionLease is not null)
                {
                    await projectionLease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private RuntimePackageExecutionRuntimeBinding? SelectInstalledRuntimeBinding(
        string packageId,
        SunderPackageTargetKey targetKey,
        SunderPackageTargetManifest target,
        IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>? bindings,
        ICollection<string> errors)
    {
        var matches = (bindings ?? [])
            .Where(binding => binding is not null
                              && string.Equals(binding.Role, targetKey.Role, StringComparison.Ordinal)
                              && string.Equals(binding.Rid, targetKey.Rid, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            errors.Add(
                $"Installed package '{packageId}' does not carry one exact execution-runtime binding for target '{targetKey}' that satisfies its strict manifest requirement.");
            return null;
        }

        return ValidateEphemeralRuntimeBinding(
            packageId,
            targetKey,
            target,
            RuntimePackageExecutionRuntimeBinding.FromInstalled(matches[0]),
            errors);
    }

    private static RuntimePackageExecutionRuntimeBinding? ValidateEphemeralRuntimeBinding(
        string packageId,
        SunderPackageTargetKey targetKey,
        SunderPackageTargetManifest target,
        RuntimePackageExecutionRuntimeBinding binding,
        ICollection<string> errors)
    {
        var requirement = target.ExecutionRuntime;
        if (!string.Equals(binding.Role, targetKey.Role, StringComparison.Ordinal)
            || !string.Equals(binding.Rid, targetKey.Rid, StringComparison.Ordinal)
            || requirement is null
            || binding.Requirement != new RuntimeExecutionRuntimeRequirement(requirement.Id!, requirement.VersionRange!, requirement.ModuleAbi!)
            || !string.Equals(binding.Runtime.RuntimeId, requirement.Id, StringComparison.Ordinal)
            || !string.Equals(binding.Runtime.ModuleAbi, requirement.ModuleAbi, StringComparison.Ordinal)
            || !Sunder.Sdk.Packaging.PackageVersionRange.IsSatisfiedBy(
                binding.Runtime.VendorVersion,
                requirement.VersionRange!))
        {
            errors.Add(
                $"Installed package '{packageId}' does not carry one exact execution-runtime binding for target '{targetKey}' that satisfies its strict manifest requirement.");
            return null;
        }

        return binding;
    }

    private static async Task<ValidatedPackage?> ValidateSourceAsync(
        string sourceRoot,
        string label,
        ICollection<string> errors,
        CancellationToken cancellationToken,
        PackageSessionStartupTracePackage? startupTrace)
    {
        var validation = startupTrace is null
            ? await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(
                sourceRoot,
                cancellationToken)
            : await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(
                sourceRoot,
                cancellationToken,
                startupTrace);
        if (!validation.Success || validation.Manifest is null || validation.ContentIndex is null)
        {
            foreach (var error in validation.Errors)
            {
                errors.Add($"{label} '{sourceRoot}' is invalid: {error}");
            }
            return null;
        }
        try
        {
            PackageManifestAdmissionPolicy.EnsureSupported(validation.Manifest);
        }
        catch (InvalidDataException exception)
        {
            errors.Add($"{label} '{sourceRoot}' is invalid: {exception.Message}");
            return null;
        }
        return new ValidatedPackage(validation.Manifest, validation.ContentIndex);
    }

    private async Task<ValidatedPackage?> ValidateInstalledSourceAsync(
        InstalledPackageRecord package,
        string physicalSourceRoot,
        ICollection<string> errors,
        CancellationToken cancellationToken,
        PackageSessionStartupTracePackage? startupTrace)
    {
        var validationStarted = Stopwatch.GetTimestamp();
        var outcome = "Failed";
        try
        {
            if (!Directory.Exists(physicalSourceRoot))
            {
                errors.Add($"Installed package folder '{physicalSourceRoot}' does not exist.");
                return null;
            }
            var validation = await ReadTrustedInstalledMetadataAsync(
                physicalSourceRoot,
                package.PackageId,
                errors,
                cancellationToken,
                startupTrace);
            if (validation is null)
            {
                return null;
            }
            if (!string.Equals(validation.Manifest.Id, package.PackageId, StringComparison.Ordinal)
                || !string.Equals(validation.Manifest.Version, package.Version, StringComparison.Ordinal)
                || !string.Equals(validation.Manifest.Name, package.Name, StringComparison.Ordinal)
                || !string.Equals(validation.Manifest.Summary, package.Summary, StringComparison.Ordinal)
                || !string.Equals(validation.Manifest.Icon, package.Icon, StringComparison.Ordinal))
            {
                errors.Add($"Installed package '{package.PackageId}' catalog identity does not match its strict manifest.");
                return null;
            }
            if (!string.Equals(validation.ContentIdentity, package.ContentIdentity, StringComparison.Ordinal)
                || !InventoryMatches(validation.ContentIndex, package.ContentInventory))
            {
                errors.Add($"Installed package '{package.PackageId}' content identity or inventory does not match its strict package content.");
                return null;
            }
            try { InstalledPackageProjection.ValidateScope(InstalledPackageProjection.Keys(package), validation.Manifest); }
            catch (InvalidDataException exception) { errors.Add(exception.Message); return null; }
            outcome = "Succeeded";
            return new ValidatedPackage(validation.Manifest, validation.ContentIndex);
        }
        catch (OperationCanceledException)
        {
            outcome = "Canceled";
            throw;
        }
        finally
        {
            if (string.Equals(outcome, "Succeeded", StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Validated {PackageSourceKind} package {PackageId} in {ElapsedMilliseconds} ms",
                    PackageSourceKind.Installed,
                    package.PackageId,
                    Stopwatch.GetElapsedTime(validationStarted).TotalMilliseconds);
            }
            else
            {
                _logger.LogWarning(
                    "Validation for {PackageSourceKind} package {PackageId} completed with {ValidationOutcome} after {ElapsedMilliseconds} ms",
                    PackageSourceKind.Installed,
                    package.PackageId,
                    outcome,
                    Stopwatch.GetElapsedTime(validationStarted).TotalMilliseconds);
            }
        }
    }

    private static async Task<TrustedInstalledMetadata?> ReadTrustedInstalledMetadataAsync(
        string sourceRoot,
        string packageId,
        ICollection<string> errors,
        CancellationToken cancellationToken,
        PackageSessionStartupTracePackage? startupTrace)
    {
        try
        {
            var manifestPath = Path.Combine(
                sourceRoot,
                SunderPackageFormat.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
            var contentIndexPath = Path.Combine(
                sourceRoot,
                SunderPackageFormat.ContentIndexPath.Replace('/', Path.DirectorySeparatorChar));
            EnsureTrustedMetadataPath(sourceRoot, manifestPath);
            EnsureTrustedMetadataPath(sourceRoot, contentIndexPath);
            var manifestBytes = await ReadBoundedMetadataAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var contentIndexBytes = await ReadBoundedMetadataAsync(contentIndexPath, cancellationToken).ConfigureAwait(false);
            startupTrace?.AddIo(
                bytesRead: manifestBytes.LongLength + contentIndexBytes.LongLength,
                bytesHashed: manifestBytes.LongLength + contentIndexBytes.LongLength);
            var manifest = StrictJsonSerializer.Deserialize<SunderPackageManifest>(
                               manifestBytes,
                               InstalledMetadataJsonOptions)
                           ?? throw new InvalidDataException("Installed package manifest is empty.");
            PackageManifestAdmissionPolicy.EnsureSupported(manifest);
            var contentIndex = JsonSerializer.Deserialize<SunderPackageContentIndex>(
                                   contentIndexBytes,
                                   InstalledMetadataJsonOptions)
                               ?? throw new InvalidDataException("Installed package content index is empty.");
            using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            identity.AppendData(SHA256.HashData(manifestBytes));
            identity.AppendData(SHA256.HashData(contentIndexBytes));
            return new TrustedInstalledMetadata(
                manifest,
                contentIndex,
                Convert.ToHexStringLower(identity.GetHashAndReset()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or InvalidDataException)
        {
            errors.Add($"Installed package '{packageId}' metadata receipt is invalid: {exception.Message}");
            return null;
        }
    }

    private static async Task<byte[]> ReadBoundedMetadataAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var maximumBytes = SunderArchiveExtractionOptions.Default.MaxMetadataJsonBytes;
        if (!info.Exists || info.Length is <= 0 || info.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"Installed package metadata '{path}' must contain between 1 and {maximumBytes} bytes.");
        }
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureTrustedMetadataPath(string sourceRoot, string path)
    {
        var root = Path.GetFullPath(sourceRoot);
        var candidate = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar,
                comparison))
        {
            throw new InvalidDataException("Installed package metadata escaped its immutable package root.");
        }
        for (var current = candidate; !string.Equals(current, root, comparison); current = Path.GetDirectoryName(current)!)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Installed package metadata contains a linked path.");
            }
        }
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Installed package root is a linked path.");
        }
    }

    private static RuntimePackageActivationState CreateActivationState(
        SunderPackageManifest manifest,
        SunderPackageTargetKey? selectedTargetKey,
        SunderPackageTargetManifest? selectedTarget,
        IReadOnlyList<SunderPackageTargetKey>? acquiredTargets = null)
        => new(
            manifest.Id!,
            manifest.Name!,
            manifest.Version!,
            PackageTargetSelection.GetHostRoles(manifest, acquiredTargets),
            manifest.Icon,
            selectedTargetKey,
            selectedTarget,
            (manifest.UsesContracts ?? []).Where(use => use is not null
                && (acquiredTargets is null || acquiredTargets.Any(key => key.Role == use.Role))).ToArray());

    internal static bool InventoryMatches(
        SunderPackageContentIndex contentIndex,
        IReadOnlyList<InstalledPackageContentRecord> inventory)
    {
        var indexed = (contentIndex.Files ?? [])
            .Select(static entry => (entry!.Path!, entry.Sha256!, entry.Size))
            .OrderBy(static entry => entry.Item1, StringComparer.Ordinal)
            .ToArray();
        var installed = inventory
            .Select(static entry => (entry.Path, entry.Sha256, entry.Size))
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        return indexed.SequenceEqual(installed);
    }

    internal static string ComputeContentIdentity(
        string sourceRoot,
        PackageSessionStartupTracePackage? startupTrace = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relativePath in new[] { SunderPackageFormat.ManifestPath, SunderPackageFormat.ContentIndexPath })
        {
            var path = Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            using var stream = File.OpenRead(path);
            var startPosition = stream.Position;
            try
            {
                hash.AppendData(SHA256.HashData(stream));
            }
            finally
            {
                try
                {
                    var processed = stream.Position - startPosition;
                    startupTrace?.AddIo(bytesRead: processed, bytesHashed: processed);
                }
                catch
                {
                    // Instrumentation never changes content identity outcomes.
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static async Task<IReadOnlyDictionary<string, SunderRpcContractDescriptor>> LoadRpcContractsAsync(
        string projectionFolder,
        SunderPackageManifest manifest,
        CancellationToken cancellationToken,
        PackageSessionStartupTracePackage? startupTrace = null)
    {
        PackageManifestAdmissionPolicy.EnsureSupported(manifest);
        var contracts = new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal);
        foreach (var bundle in manifest.ContractBundles ?? [])
        {
            if (bundle is null) continue;
            var descriptorPath = ArchiveRelativePath.Parse(
                bundle.DescriptorPath!,
                SunderPackageFormat.MaxLogicalPathLength,
                SunderPackageFormat.MaxLogicalPathDepth).ToPlatformPath(projectionFolder);
            var bytes = startupTrace is null
                ? await File.ReadAllBytesAsync(descriptorPath, cancellationToken)
                : await ReadAllBytesAsync(descriptorPath, cancellationToken, startupTrace);
            SunderRpcContractDescriptor descriptor;
            try
            {
                descriptor = SunderRpcContractDescriptor.Parse(bytes);
            }
            catch (SunderRpcDescriptorException exception)
            {
                throw new InvalidDataException(
                    $"Package RPC contract descriptor '{bundle.DescriptorPath}' is invalid: {exception.Message}",
                    exception);
            }
            RuntimeRpcPackageMetadata.ValidateContractDescriptor(
                bundle,
                descriptor,
                bundle.DescriptorPath!);
            contracts.Add(ContractKey(descriptor.ContractId, descriptor.Version), descriptor);
        }
        return contracts;
    }

    private static async Task<byte[]> ReadAllBytesAsync(
        string path,
        CancellationToken cancellationToken,
        PackageSessionStartupTracePackage? startupTrace)
    {
        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var destination = source.Length <= int.MaxValue
            ? new MemoryStream((int)source.Length)
            : new MemoryStream();
        try
        {
            await source.CopyToAsync(destination, cancellationToken);
            return destination.ToArray();
        }
        finally
        {
            try
            {
                startupTrace?.AddIo(bytesRead: source.Position);
            }
            catch
            {
                // Instrumentation never changes RPC descriptor loading outcomes.
            }
        }
    }

    private static string GetManifestSha256(SunderPackageContentIndex contentIndex)
        => contentIndex.Files?.Single(entry => string.Equals(
            entry?.Path,
            SunderPackageFormat.ManifestPath,
            StringComparison.Ordinal)).Sha256
           ?? throw new InvalidDataException("Package content index is missing its manifest hash.");

    internal static string ContractKey(string contractId, string version) => contractId + "\0" + version;

    private static string GetSessionId(string projectionFolder)
        => new DirectoryInfo(projectionFolder).Parent?.Parent?.Name
           ?? Guid.NewGuid().ToString("N");

    private static string ToCacheResult(PackageProjectionCacheOutcome outcome)
        => outcome switch
        {
            PackageProjectionCacheOutcome.Hit => "hit",
            PackageProjectionCacheOutcome.Miss => "miss",
            PackageProjectionCacheOutcome.CorruptRebuild => "corrupt-rebuild",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string SanitizeFolderName(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return "package";
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(folderName.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
    }

    private sealed record ValidatedPackage(
        SunderPackageManifest Manifest,
        SunderPackageContentIndex ContentIndex);

    private sealed record TrustedInstalledMetadata(
        SunderPackageManifest Manifest,
        SunderPackageContentIndex ContentIndex,
        string ContentIdentity);

    internal sealed record InstalledPackageValidation(
        SunderPackageManifest Manifest,
        SunderPackageContentIndex ContentIndex,
        RuntimePackageActivationState Activation);
}
