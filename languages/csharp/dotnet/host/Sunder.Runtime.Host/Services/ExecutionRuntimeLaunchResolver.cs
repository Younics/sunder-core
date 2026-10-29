using System.Security.Cryptography;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimePackageExecutionRuntimeBinding(
    string Role,
    string Rid,
    RuntimeExecutionRuntimeRequirement Requirement,
    RuntimeExecutionRuntimeIdentity Runtime,
    RuntimeExecutionRuntimeInstalledArtifact InstalledArtifact,
    DateTimeOffset ResolvedAtUtc)
{
    public static RuntimePackageExecutionRuntimeBinding FromInstalled(
        InstalledPackageExecutionRuntimeBindingRecord binding)
        => new(
            binding.Role,
            binding.Rid,
            binding.Requirement,
            binding.Runtime,
            binding.InstalledArtifact,
            binding.ResolvedAtUtc);

    public ExecutionRuntimeIdentity Identity => ExecutionRuntimeIdentity.FromContract(Runtime);

    public bool MatchesReceipt(ExecutionRuntimeReceiptV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return receipt.Identity == Identity
               && string.Equals(receipt.ArchiveSha256, InstalledArtifact.ArchiveSha256, StringComparison.Ordinal)
               && receipt.ArchiveSize == InstalledArtifact.ArchiveSize
               && string.Equals(receipt.ContentIdentity, InstalledArtifact.ContentIdentity, StringComparison.Ordinal)
               && receipt.Provenance == InstalledArtifact.Provenance;
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
    private readonly ExecutionRuntimePhaseTelemetry _telemetry;
    private readonly PackageLifecyclePolicyCache? _activePolicies;

    public ExecutionRuntimeLaunchResolver(
        ExecutionRuntimeStore store,
        TimeProvider timeProvider,
        ExecutionRuntimePhaseTelemetry? telemetry = null,
        PackageLifecyclePolicyCache? activePolicies = null)
        : this(new ExecutionRuntimeLaunchStore(store), telemetry, activePolicies)
    {
    }

    internal ExecutionRuntimeLaunchResolver(IExecutionRuntimeLaunchStore store)
        : this(store, telemetry: null)
    {
    }

    internal ExecutionRuntimeLaunchResolver(
        IExecutionRuntimeLaunchStore store,
        ExecutionRuntimePhaseTelemetry? telemetry = null,
        PackageLifecyclePolicyCache? activePolicies = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
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
        if (!OfficialRuntimeSourcePolicy.EligibleVersion(binding.Identity.SourceId, binding.Identity.VendorVersion))
            throw Invalid(package, "requires a runtime major outside Sunder's supported range");
        phase.SetIdentity(binding.Identity);

        var receipt = await _store.GetReceiptAsync(binding.Identity, cancellationToken).ConfigureAwait(false)
                      ?? throw Invalid(package, "execution-runtime receipt is missing");
        if (!binding.MatchesReceipt(receipt))
        {
            throw Invalid(package, "execution-runtime receipt does not match its exact committed binding");
        }
        if (_activePolicies is not null) new PackagePolicyAdmission(_activePolicies).Require(package.Source);

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
                OfficialRuntimeSourcePolicy.MaxPathLength,
                32)
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
                    OfficialRuntimeSourcePolicy.EntryPointToken,
                    StringComparison.Ordinal)
                    ? entryPoint
                    : argument)
                .ToArray();
            if (arguments.Any(argument => string.Equals(
                    argument,
                    OfficialRuntimeSourcePolicy.EntryPointToken,
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
                activationWarning: null,
                phase.CorrelationId);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
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
            || !string.Equals(binding.Requirement.RuntimeId, requirement.Id, StringComparison.Ordinal)
            || !string.Equals(binding.Requirement.VersionRange, requirement.VersionRange, StringComparison.Ordinal)
            || !string.Equals(binding.Requirement.ModuleAbi, requirement.ModuleAbi, StringComparison.Ordinal)
            || !string.Equals(binding.Runtime.RuntimeId, requirement.Id, StringComparison.Ordinal)
            || !string.Equals(binding.Runtime.Rid, targetKey.Rid, StringComparison.Ordinal)
            || !string.Equals(binding.Runtime.ModuleAbi, requirement.ModuleAbi, StringComparison.Ordinal)
            || !PackageVersionRange.IsSatisfiedBy(binding.Runtime.VendorVersion, requirement.VersionRange!))
        {
            throw Invalid(package, "execution-runtime binding does not satisfy the selected role/RID target requirement");
        }
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
