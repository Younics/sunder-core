using System.Security.Cryptography;
using System.Globalization;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimePackageExecutionRuntimeBinding(
    string Role,
    string Rid,
    string RuntimeId,
    string RuntimeVersion,
    int RuntimePackagingRevision,
    string ModuleAbi,
    string RuntimeManifestSha256,
    string RuntimeArtifactSha256,
    long RuntimeArtifactSize,
    string RegistryOrigin,
    string AcquisitionSource,
    string SigningKeyId,
    long TrustStoreGeneration,
    long RuntimePolicyRevision,
    string RuntimeLifecycleState,
    DateTimeOffset PolicyObservedAtUtc,
    DateTimeOffset ResolvedAtUtc)
{
    public static RuntimePackageExecutionRuntimeBinding FromInstalled(
        InstalledPackageExecutionRuntimeBindingRecord binding)
        => new(
            binding.Role,
            binding.Rid,
            binding.RuntimeId,
            binding.RuntimeVersion,
            binding.RuntimePackagingRevision,
            binding.ModuleAbi,
            binding.RuntimeManifestSha256,
            binding.RuntimeArtifactSha256,
            binding.RuntimeArtifactSize,
            binding.RegistryOrigin,
            binding.AcquisitionSource,
            binding.SigningKeyId,
            binding.TrustStoreGeneration,
            binding.RuntimePolicyRevision,
            binding.RuntimeLifecycleState,
            binding.PolicyObservedAtUtc,
            binding.ResolvedAtUtc);

    public ExecutionRuntimeIdentity Identity => new(
        RuntimeId,
        RuntimeVersion,
        RuntimePackagingRevision,
        Rid,
        ModuleAbi);

    public bool MatchesReceipt(ExecutionRuntimeReceiptV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var trustAndSourceMatch = AcquisitionSource switch
        {
            RuntimeExecutionRuntimeAcquisitionSources.Registry
                or RuntimeExecutionRuntimeAcquisitionSources.LocalImport =>
                receipt.TrustEnvironment == ExecutionRuntimeTrustEnvironment.Production,
            RuntimeExecutionRuntimeAcquisitionSources.Development =>
                receipt.TrustEnvironment == ExecutionRuntimeTrustEnvironment.Development,
            _ => false,
        };
        var originMatches = AcquisitionSource switch
        {
            RuntimeExecutionRuntimeAcquisitionSources.Registry
                or RuntimeExecutionRuntimeAcquisitionSources.Development => string.Equals(
                    receipt.RegistryOrigin,
                    RegistryOrigin,
                    StringComparison.Ordinal),
            RuntimeExecutionRuntimeAcquisitionSources.LocalImport => receipt.RegistryOrigin is null,
            _ => false,
        };
        return receipt.Identity == Identity
               && string.Equals(receipt.WorkerProtocol, SunderExecutionRuntimeFormat.WorkerProtocol, StringComparison.Ordinal)
               && string.Equals(receipt.ManifestSha256, RuntimeManifestSha256, StringComparison.Ordinal)
               && string.Equals(receipt.ArtifactSha256, RuntimeArtifactSha256, StringComparison.Ordinal)
               && receipt.ArtifactSize == RuntimeArtifactSize
               && string.Equals(receipt.SigningKeyId, SigningKeyId, StringComparison.Ordinal)
               && receipt.TrustGeneration == TrustStoreGeneration
               && trustAndSourceMatch
               && string.Equals(receipt.AcquisitionSource, AcquisitionSource, StringComparison.Ordinal)
               && originMatches;
    }
}

internal sealed class ResolvedExecutionRuntimeLaunch : IAsyncDisposable
{
    private readonly IExecutionRuntimeProcessLease _lease;
    private int _leaseTransferred;

    public ResolvedExecutionRuntimeLaunch(
        string runtimeRootPath,
        string launcherPath,
        IReadOnlyList<string> argumentTokens,
        IExecutionRuntimeProcessLease lease,
        ExecutionRuntimeIdentity? identity = null,
        string? activationWarning = null,
        Guid? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherPath);
        ArgumentNullException.ThrowIfNull(argumentTokens);
        ArgumentNullException.ThrowIfNull(lease);
        if (!Path.IsPathFullyQualified(runtimeRootPath)
            || !Path.IsPathFullyQualified(launcherPath)
            || argumentTokens.Any(static argument => argument is null)
            || activationWarning is { Length: > 1024 }
            || activationWarning?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("An execution-runtime launch requires absolute paths and non-null argument tokens.");
        }

        RuntimeRootPath = Path.GetFullPath(runtimeRootPath);
        LauncherPath = Path.GetFullPath(launcherPath);
        ArgumentTokens = Array.AsReadOnly(argumentTokens.ToArray());
        Identity = identity;
        ActivationWarning = activationWarning;
        CorrelationId = correlationId is { } supplied && supplied != Guid.Empty
            ? supplied
            : Guid.NewGuid();
        _lease = lease;
        if (!PathsEqual(RuntimeRootPath, lease.RuntimeRootPath))
        {
            throw new ArgumentException("The resolved launch and durable lease name different runtime roots.", nameof(lease));
        }
    }

    public string RuntimeRootPath { get; }
    public string LauncherPath { get; }
    public IReadOnlyList<string> ArgumentTokens { get; }
    public ExecutionRuntimeIdentity? Identity { get; }
    public string? ActivationWarning { get; }
    public Guid CorrelationId { get; }
    public string LeaseId => _lease.LeaseId;

    internal IExecutionRuntimeProcessLease TransferLease()
    {
        if (Interlocked.Exchange(ref _leaseTransferred, 1) != 0)
        {
            throw new InvalidOperationException("The resolved execution-runtime lease was already transferred.");
        }
        return _lease;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _leaseTransferred, 1, 0) == 0)
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

internal interface IExecutionRuntimeLaunchResolver
{
    Task<ResolvedExecutionRuntimeLaunch> ResolveAsync(
        PreparedRuntimePackage package,
        Guid activationId,
        CancellationToken cancellationToken);
}

internal interface IEphemeralExecutionRuntimeBindingResolver
{
    Task<ResolvedEphemeralExecutionRuntimeBinding> ResolveEphemeralBindingAsync(
        SunderPackageTargetKey targetKey,
        SunderPackageTargetManifest target,
        CancellationToken cancellationToken);
}

internal interface ITestOnlyInstalledExecutionRuntimeBindingResolver
    : IEphemeralExecutionRuntimeBindingResolver
{
}

internal sealed class ResolvedEphemeralExecutionRuntimeBinding(
    RuntimePackageExecutionRuntimeBinding binding,
    IAsyncDisposable? retention = null) : IAsyncDisposable
{
    private IAsyncDisposable? _retention = retention;

    public RuntimePackageExecutionRuntimeBinding Binding { get; } = binding;

    public async ValueTask DisposeAsync()
    {
        var retained = Interlocked.Exchange(ref _retention, null);
        if (retained is not null)
        {
            await retained.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal interface IExecutionRuntimeLaunchStore
{
    ExecutionRuntimeStoreLayout Layout { get; }

    Task<ExecutionRuntimeReceiptV1?> GetReceiptAsync(
        ExecutionRuntimeIdentity identity,
        CancellationToken cancellationToken);

    Task<ExecutionRuntimeVerificationResult> VerifyAsync(
        ExecutionRuntimeIdentity identity,
        bool deep,
        CancellationToken cancellationToken);

    Task<IExecutionRuntimeProcessLease> ReserveProcessLeaseAsync(
        ExecutionRuntimeLeaseOwner owner,
        string contentIdentity,
        string runtimeRootPath,
        string executablePath,
        CancellationToken cancellationToken);

    Task<IExecutionRuntimeProcessLease> ReserveProcessLeaseAsync(
        ExecutionRuntimeLeaseOwner owner,
        string contentIdentity,
        string runtimeRootPath,
        string executablePath,
        ExecutionRuntimeIdentity identity,
        CancellationToken cancellationToken)
        => ReserveProcessLeaseAsync(
            owner,
            contentIdentity,
            runtimeRootPath,
            executablePath,
            cancellationToken);
}

internal sealed class ExecutionRuntimeLaunchResolver : IExecutionRuntimeLaunchResolver
{
    private readonly IExecutionRuntimeLaunchStore _store;
    private readonly ExecutionRuntimeLifecyclePolicyCache? _lifecyclePolicies;
    private readonly TimeProvider _timeProvider;
    private readonly ExecutionRuntimePhaseTelemetry _telemetry;
    private readonly PackageLifecyclePolicyCache? _activePolicies;

    public ExecutionRuntimeLaunchResolver(
        ExecutionRuntimeStore store,
        ExecutionRuntimeLifecyclePolicyCache lifecyclePolicies,
        TimeProvider timeProvider,
        ExecutionRuntimePhaseTelemetry? telemetry = null,
        PackageLifecyclePolicyCache? activePolicies = null)
        : this(new ExecutionRuntimeLaunchStore(store), lifecyclePolicies, timeProvider, telemetry, activePolicies)
    {
    }

    internal ExecutionRuntimeLaunchResolver(IExecutionRuntimeLaunchStore store)
        : this(store, lifecyclePolicies: null, timeProvider: null, telemetry: null)
    {
    }

    internal ExecutionRuntimeLaunchResolver(
        IExecutionRuntimeLaunchStore store,
        ExecutionRuntimeLifecyclePolicyCache? lifecyclePolicies,
        TimeProvider? timeProvider,
        ExecutionRuntimePhaseTelemetry? telemetry = null,
        PackageLifecyclePolicyCache? activePolicies = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _lifecyclePolicies = lifecyclePolicies;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _telemetry = telemetry ?? ExecutionRuntimePhaseTelemetry.Disabled;
        _activePolicies = activePolicies;
    }

    public async Task<ResolvedExecutionRuntimeLaunch> ResolveAsync(
        PreparedRuntimePackage package,
        Guid activationId,
        CancellationToken cancellationToken)
    {
        using var operation = _telemetry.BeginOperation();
        using var phase = _telemetry.BeginPhase(ExecutionRuntimePhaseNames.LaunchResolve);
        try
        {
            var result = await ResolveCoreAsync(package, activationId, phase, cancellationToken)
                .ConfigureAwait(false);
            phase.Succeeded();
            return result;
        }
        catch (OperationCanceledException)
        {
            phase.Canceled();
            throw;
        }
        catch (Exception exception)
        {
            phase.Failed(exception);
            throw;
        }
    }

    private async Task<ResolvedExecutionRuntimeLaunch> ResolveCoreAsync(
        PreparedRuntimePackage package,
        Guid activationId,
        ExecutionRuntimePhaseTelemetry.PhaseScope phase,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (activationId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty Runtime activation id is required.", nameof(activationId));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var targetKey = package.SelectedTargetKey
                        ?? throw Invalid(package, "does not select an exact Runtime target");
        var target = package.SelectedTarget
                     ?? throw Invalid(package, "does not carry its selected Runtime target");
        var requirement = target.ExecutionRuntime
                          ?? throw Invalid(package, "does not declare an execution-runtime requirement");
        var binding = package.ExecutionRuntimeBinding
                      ?? throw Invalid(
                          package,
                          package.Source.Kind == PackageSourceKind.Dev
                              ? "does not have an explicitly supplied ephemeral execution-runtime binding"
                               : "does not carry its committed execution-runtime binding");

        ValidateTargetBinding(package, targetKey, requirement, binding);
        ValidatePolicyEvidence(package, binding);
        phase.SetIdentity(binding.Identity);
        if (_lifecyclePolicies is null && _activePolicies is null
            && binding.RuntimeLifecycleState is not (
                ExecutionRuntimeLifecycleStates.Supported or ExecutionRuntimeLifecycleStates.Deprecated))
        {
            throw Invalid(
                package,
                $"execution runtime lifecycle state '{binding.RuntimeLifecycleState}' does not permit activation");
        }

        var receipt = await _store.GetReceiptAsync(binding.Identity, cancellationToken).ConfigureAwait(false)
                      ?? throw Invalid(package, "execution-runtime receipt is missing");
        if (!binding.MatchesReceipt(receipt))
        {
            throw Invalid(package, "execution-runtime receipt does not match its exact committed binding");
        }
        var activationWarning = await ValidateLifecycleAsync(package, binding, receipt, cancellationToken)
            .ConfigureAwait(false);

        var verification = await _store.VerifyAsync(binding.Identity, deep: false, cancellationToken)
            .ConfigureAwait(false);
        if (!verification.Healthy)
        {
            throw Invalid(
                package,
                $"execution-runtime receipt/root failed warm verification: {verification.Message ?? "unknown verification failure"}");
        }

        var runtimeRoot = _store.Layout.GetContentRootPath(receipt.ContentIdentity);
        PackageStorePathGuard.EnsureSafe(runtimeRoot, _store.Layout.ContentRootPath);
        var launcherPath = ArchiveRelativePath.Parse(
                receipt.LauncherPath,
                SunderExecutionRuntimeFormat.MaxArchivePathLength,
                SunderExecutionRuntimeFormat.MaxArchivePathDepth)
            .ToPlatformPath(runtimeRoot);
        PackageStorePathGuard.EnsureSafe(launcherPath, runtimeRoot);
        var lease = await _store.ReserveProcessLeaseAsync(
                new ExecutionRuntimeLeaseOwner(package.PackageId, package.SessionId, activationId),
                receipt.ContentIdentity,
                runtimeRoot,
                launcherPath,
                receipt.Identity,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var entryPoint = await ValidateProjectedPackageAsync(package, targetKey, target, cancellationToken)
                .ConfigureAwait(false);
            var arguments = receipt.LauncherArguments
                .Select(argument => string.Equals(
                    argument,
                    SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder,
                    StringComparison.Ordinal)
                    ? entryPoint
                    : argument)
                .ToArray();
            if (arguments.Any(argument => string.Equals(
                    argument,
                    SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder,
                    StringComparison.Ordinal)))
            {
                throw Invalid(package, "execution-runtime launch recipe contains an unresolved package entry-point token");
            }

            return new ResolvedExecutionRuntimeLaunch(
                runtimeRoot,
                launcherPath,
                arguments,
                lease,
                receipt.Identity,
                activationWarning,
                phase.CorrelationId);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidatePolicyEvidence(
        PreparedRuntimePackage package,
        RuntimePackageExecutionRuntimeBinding binding)
    {
        if (binding.RuntimePolicyRevision <= 0
            || binding.PolicyObservedAtUtc == default
            || binding.ResolvedAtUtc == default
            || binding.RuntimeLifecycleState is not (
                ExecutionRuntimeLifecycleStates.Supported
                or ExecutionRuntimeLifecycleStates.Deprecated
                or ExecutionRuntimeLifecycleStates.Retired
                or ExecutionRuntimeLifecycleStates.Revoked))
        {
            throw Invalid(package, "does not carry valid execution-runtime policy evidence");
        }
    }

    private static void ValidateTargetBinding(
        PreparedRuntimePackage package,
        SunderPackageTargetKey targetKey,
        SunderPackageExecutionRuntimeManifest requirement,
        RuntimePackageExecutionRuntimeBinding binding)
    {
        if (!string.Equals(targetKey.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal)
            || !string.Equals(binding.Role, targetKey.Role, StringComparison.Ordinal)
            || !string.Equals(binding.Rid, targetKey.Rid, StringComparison.Ordinal)
            || !string.Equals(binding.RuntimeId, requirement.Id, StringComparison.Ordinal)
            || !string.Equals(binding.ModuleAbi, requirement.ModuleAbi, StringComparison.Ordinal)
            || !PackageVersionRange.IsSatisfiedBy(binding.RuntimeVersion, requirement.VersionRange!))
        {
            throw Invalid(package, "execution-runtime binding does not satisfy the selected role/RID target requirement");
        }
    }

    private async Task<string?> ValidateLifecycleAsync(
        PreparedRuntimePackage package,
        RuntimePackageExecutionRuntimeBinding binding,
        ExecutionRuntimeReceiptV1 receipt,
        CancellationToken cancellationToken)
    {
        if (binding.RuntimeLifecycleState == ExecutionRuntimeLifecycleStates.Revoked)
        {
            throw Invalid(package, "execution runtime lifecycle state 'revoked' does not permit activation");
        }
        var stagedAuthority = package.ExecutionRuntimePolicyAuthority;
        if (stagedAuthority is not null
            && (!stagedAuthority.MatchesReceipt(receipt)
                || stagedAuthority.RuntimePolicyRevision < binding.RuntimePolicyRevision
                || !string.Equals(
                    stagedAuthority.RegistryOrigin,
                    binding.RegistryOrigin,
                    StringComparison.Ordinal)))
        {
            throw Invalid(
                package,
                "staged execution-runtime policy authority does not match its exact binding and immutable receipt");
        }
        if (stagedAuthority is { TrustDisposition: ExecutionRuntimeTrustDispositions.Denied }
            || stagedAuthority?.Lifecycle.State == ExecutionRuntimeLifecycleStates.Revoked)
            ValidateCachedPolicy(package, stagedAuthority!);
        if (_lifecyclePolicies is not null)
        {
            await _lifecyclePolicies.InitializeAsync(cancellationToken).ConfigureAwait(false);
            if (_lifecyclePolicies.CaptureSnapshot().TryGet(receipt.Identity, out var policy))
            {
                if (policy is null || !policy.MatchesReceipt(receipt))
                {
                    throw Invalid(
                        package,
                        "cached execution-runtime lifecycle policy does not match its immutable receipt");
                }
                if (_activePolicies is not null && binding.AcquisitionSource != RuntimeExecutionRuntimeAcquisitionSources.Development)
                {
                    if (policy.TrustDisposition == ExecutionRuntimeTrustDispositions.Denied || policy.Lifecycle.State == ExecutionRuntimeLifecycleStates.Revoked)
                        ValidateCachedPolicy(package, policy);
                    return ValidateActivePolicy(package, binding, receipt.Identity, policy.Lifecycle.State == ExecutionRuntimeLifecycleStates.Retired);
                }
                var wouldWeakenRetiredBinding =
                    binding.RuntimeLifecycleState == ExecutionRuntimeLifecycleStates.Retired
                    && policy.Lifecycle.State is ExecutionRuntimeLifecycleStates.Supported
                        or ExecutionRuntimeLifecycleStates.Deprecated;
                if (!wouldWeakenRetiredBinding
                    && (policy.RuntimePolicyRevision >= binding.RuntimePolicyRevision
                        || policy.TrustDisposition == ExecutionRuntimeTrustDispositions.Denied
                        || policy.Lifecycle.State is ExecutionRuntimeLifecycleStates.Retired
                            or ExecutionRuntimeLifecycleStates.Revoked))
                {
                    ValidateCachedPolicy(package, policy);
                    return CreateActivationWarning(package, receipt.Identity, policy.Lifecycle);
                }
            }
        }
        if (_activePolicies is not null && binding.AcquisitionSource != RuntimeExecutionRuntimeAcquisitionSources.Development)
            return ValidateActivePolicy(package, binding, receipt.Identity, stagedAuthority?.Lifecycle.State == ExecutionRuntimeLifecycleStates.Retired);
        if (stagedAuthority is not null)
        {
            ValidateCachedPolicy(package, stagedAuthority);
            return CreateActivationWarning(package, receipt.Identity, stagedAuthority.Lifecycle);
        }
        if (binding.RuntimeLifecycleState is not (
                ExecutionRuntimeLifecycleStates.Supported or ExecutionRuntimeLifecycleStates.Deprecated))
        {
            throw Invalid(
                package,
                $"execution runtime lifecycle state '{binding.RuntimeLifecycleState}' does not permit activation");
        }
        return binding.RuntimeLifecycleState == ExecutionRuntimeLifecycleStates.Deprecated
            ? CreateActivationWarning(package, receipt.Identity, lifecycle: null)
            : null;
    }

    private string? ValidateActivePolicy(PreparedRuntimePackage package, RuntimePackageExecutionRuntimeBinding binding, ExecutionRuntimeIdentity identity, bool knownRetired)
    {
        var decision = PackagePolicyAdmission.RequireBindingPolicy(_activePolicies!, binding);
        if ((knownRetired || binding.RuntimeLifecycleState == ExecutionRuntimeLifecycleStates.Retired) && decision.LifecycleState != ExecutionRuntimeLifecycleStates.Retired)
            throw Invalid(package, "active policy cannot erase a known retired runtime");
        return CreateActivationWarning(package, identity, new RegistryExecutionRuntimeLifecycle(decision.LifecycleState,
            default, null, null, null, decision.ActivationBlockedAtUtc, decision.RevokedAtUtc, null));
    }

    private void ValidateCachedPolicy(
        PreparedRuntimePackage package,
        ExecutionRuntimeLifecyclePolicyEntryV1 policy)
    {
        if (policy.TrustDisposition == ExecutionRuntimeTrustDispositions.Denied)
        {
            throw Invalid(package, "Registry trust policy denies this exact execution runtime");
        }
        ValidateCachedLifecycle(package, policy.Lifecycle);
    }

    private void ValidateCachedLifecycle(
        PreparedRuntimePackage package,
        RegistryExecutionRuntimeLifecycle lifecycle)
    {
        var now = _timeProvider.GetUtcNow();
        if (lifecycle.State == ExecutionRuntimeLifecycleStates.Revoked)
        {
            throw Invalid(package, "execution runtime lifecycle state 'revoked' does not permit activation");
        }
        if (lifecycle.ActivationBlockedAtUtc is { } activationBlocked
            && activationBlocked <= now)
        {
            throw Invalid(package, "execution runtime activation is blocked by its lifecycle deadline");
        }
        if (lifecycle.State is ExecutionRuntimeLifecycleStates.Supported
            or ExecutionRuntimeLifecycleStates.Deprecated)
        {
            return;
        }
        if (lifecycle.State == ExecutionRuntimeLifecycleStates.Retired
            && (lifecycle.ActivationBlockedAtUtc is null
                || lifecycle.ActivationBlockedAtUtc > now))
        {
            return;
        }
        throw Invalid(
            package,
            lifecycle.State == ExecutionRuntimeLifecycleStates.Retired
                ? "execution runtime is retired and its activation deadline has elapsed"
                : $"execution runtime lifecycle state '{lifecycle.State}' does not permit activation");
    }

    private static string? CreateActivationWarning(
        PreparedRuntimePackage package,
        ExecutionRuntimeIdentity identity,
        RegistryExecutionRuntimeLifecycle? lifecycle)
    {
        var state = lifecycle?.State ?? ExecutionRuntimeLifecycleStates.Deprecated;
        var subject = $"Package '{package.PackageId}' uses {state} execution runtime '{identity.RuntimeId}' version '{identity.Version}' packaging revision {identity.PackagingRevision} for RID '{identity.Rid}'.";
        if (state == ExecutionRuntimeLifecycleStates.Deprecated)
        {
            return subject + " Update the package or rebind it to a supported execution runtime.";
        }
        if (state != ExecutionRuntimeLifecycleStates.Retired)
        {
            return null;
        }
        if (lifecycle?.ActivationBlockedAtUtc is { } deadline)
        {
            return subject
                   + $" Activation is allowed only until {deadline.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}."
                   + " Update the package or rebind it to a supported execution runtime before that deadline.";
        }
        return subject + " Update the package or rebind it to a supported execution runtime before activation is blocked.";
    }

    private static async Task<string> ValidateProjectedPackageAsync(
        PreparedRuntimePackage package,
        SunderPackageTargetKey targetKey,
        SunderPackageTargetManifest target,
        CancellationToken cancellationToken)
    {
        var manifest = package.Source.Manifest
                       ?? throw Invalid(package, "is missing its strict package manifest");
        var contentIndex = package.Source.ContentIndex
                           ?? throw Invalid(package, "is missing its strict package content inventory");
        if (package.Source.ContentIdentity is not { } packageContentIdentity)
        {
            throw Invalid(package, "is missing its strict package content identity");
        }
        ExecutionRuntimeStoreLayout.ValidateSha256(packageContentIdentity, nameof(package.Source.ContentIdentity));
        var projectionRoot = Path.GetFullPath(package.ShadowFolder);
        if (!Directory.Exists(projectionRoot))
        {
            throw Invalid(package, "projected package root is missing");
        }
        PackageStorePathGuard.EnsureSafe(projectionRoot, projectionRoot);

        var entryPointRelative = ArchiveRelativePath.Parse(
            target.EntryPoint!,
            SunderPackageFormat.MaxLogicalPathLength,
            SunderPackageFormat.MaxLogicalPathDepth);
        var entryPoint = entryPointRelative.ToPlatformPath(projectionRoot);
        if (package.EntryAssemblyPath is null || !PathsEqual(entryPoint, package.EntryAssemblyPath))
        {
            throw Invalid(package, "projected package entry point does not match the selected target");
        }
        PackageStorePathGuard.EnsureSafe(entryPoint, projectionRoot);

        var inventory = (contentIndex.Files ?? throw Invalid(package, "has a null package content inventory"))
            .Where(static entry => entry is not null)
            .Select(static entry => entry!)
            .ToDictionary(static entry => entry.Path!, StringComparer.Ordinal);
        var plan = SunderPackageTargetResolver.CreateProjectionPlan(manifest, contentIndex, targetKey);
        var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var projected in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logicalPath = projected.LogicalPath.ToString();
            if (!expectedPaths.Add(logicalPath)
                || !inventory.TryGetValue(projected.PhysicalPath.ToString(), out var expected))
            {
                throw Invalid(package, "projected package inventory is inconsistent with its selected target");
            }
            var path = projected.LogicalPath.ToPlatformPath(projectionRoot);
            PackageStorePathGuard.EnsureSafe(path, projectionRoot);
            await VerifyFileAsync(
                    path,
                    expected.Size,
                    expected.Sha256!,
                    $"projected package file '{logicalPath}'",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var actualPaths = Directory.EnumerateFiles(projectionRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(projectionRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.Ordinal);
        if (!actualPaths.SetEquals(expectedPaths) || !expectedPaths.Contains(entryPointRelative.ToString()))
        {
            throw Invalid(package, "projected package content does not match its exact selected-target inventory");
        }
        return Path.GetFullPath(entryPoint);
    }

    private static async Task VerifyFileAsync(
        string path,
        long expectedSize,
        string expectedSha256,
        string label,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedSize)
        {
            throw new InvalidDataException($"The {label} size does not match its immutable inventory.");
        }
        var actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The {label} hash does not match its immutable inventory.");
        }
    }

    private static InvalidDataException Invalid(PreparedRuntimePackage package, string reason)
        => new($"Package '{package.PackageId}' {reason}.");

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class ExecutionRuntimeLaunchStore(ExecutionRuntimeStore store)
        : IExecutionRuntimeLaunchStore
    {
        public ExecutionRuntimeStoreLayout Layout => store.Layout;

        public Task<ExecutionRuntimeReceiptV1?> GetReceiptAsync(
            ExecutionRuntimeIdentity identity,
            CancellationToken cancellationToken)
            => store.GetReceiptAsync(identity, cancellationToken);

        public Task<ExecutionRuntimeVerificationResult> VerifyAsync(
            ExecutionRuntimeIdentity identity,
            bool deep,
            CancellationToken cancellationToken)
            => store.VerifyAsync(identity, deep, cancellationToken);

        public Task<IExecutionRuntimeProcessLease> ReserveProcessLeaseAsync(
            ExecutionRuntimeLeaseOwner owner,
            string contentIdentity,
            string runtimeRootPath,
            string executablePath,
            CancellationToken cancellationToken)
            => store.ReserveProcessLeaseAsync(
                owner,
                contentIdentity,
                runtimeRootPath,
                executablePath,
                cancellationToken);

        public Task<IExecutionRuntimeProcessLease> ReserveProcessLeaseAsync(
            ExecutionRuntimeLeaseOwner owner,
            string contentIdentity,
            string runtimeRootPath,
            string executablePath,
            ExecutionRuntimeIdentity identity,
            CancellationToken cancellationToken)
            => store.ReserveProcessLeaseAsync(
                owner,
                contentIdentity,
                runtimeRootPath,
                executablePath,
                identity,
                cancellationToken);
    }
}

internal sealed class UnavailableExecutionRuntimeLaunchResolver : IExecutionRuntimeLaunchResolver
{
    public static UnavailableExecutionRuntimeLaunchResolver Instance { get; } = new();

    private UnavailableExecutionRuntimeLaunchResolver()
    {
    }

    public Task<ResolvedExecutionRuntimeLaunch> ResolveAsync(
        PreparedRuntimePackage package,
        Guid activationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<ResolvedExecutionRuntimeLaunch>(new InvalidOperationException(
            $"Package '{package.PackageId}' cannot activate because no exact execution-runtime launch resolver was configured."));
    }
}
