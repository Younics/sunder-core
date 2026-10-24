using System.Security.Cryptography;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal interface IRuntimeRpcCallerActivation
{
    string PackageId { get; }
    string PackageVersion { get; }
    Guid? ActivationId { get; }
    string Role { get; }
    string ManifestSha256 { get; }
    PackageSourceKind? SourceKind { get; }
    IReadOnlyDictionary<string, SunderRpcContractDescriptor> RpcContracts { get; }
    IReadOnlyList<SunderPackageContractUseManifest> RpcContractUses { get; }
    CancellationToken RetirementToken { get; }
    bool IsCurrent(PackageSessionLease sessionLease);
    bool TryAcquireCaller(int maximumCalls, out RuntimeRpcActivationLease? lease, bool observation = false);
}

internal sealed record RuntimeRpcPermissionSubject(
    string PackageId,
    string PackageVersion,
    string ManifestSha256,
    PackageSourceKind SourceKind,
    IReadOnlyList<SunderPackageContractUseManifest> ContractUses);

internal static class RuntimeRpcPermissionSubjects
{
    public static IReadOnlyList<RuntimeRpcPermissionSubject> FromSession(ActivePackageSession session)
        => session.GetActivePackageSources()
            .Where(static source => source.HostRoles != PackageHostRoles.None
                                    && source.Manifest is not null
                                    && source.ContentIndex is not null)
            .Select(static source =>
            {
                PackageManifestAdmissionPolicy.EnsureSupported(source.Manifest!);
                return new RuntimeRpcPermissionSubject(
                    source.PackageId,
                    source.Manifest!.Version!,
                    RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex!),
                    source.Kind,
                    (source.Manifest.UsesContracts ?? [])
                        .Where(use => use is not null && InstalledPackageProjection.IncludesRole(source, use.Role!)
                            && PackageCompatibilityAuthority.DevelopmentUseEnabled(source, use.Role!, use.ContractId!, use.ContractVersion!))
                        .Select(static use => use!)
                        .ToArray());
            })
            .ToArray();
}

internal sealed class RuntimeRpcAppSessionManager(
    PackageSessionState sessions,
    RuntimeRpcPolicyOptions policy,
    AppProcessPolicySessions? processPolicies = null) : IDisposable
{
    internal bool RequiresProcessSupervision => processPolicies is not null;
    private readonly object _gate = new();
    private readonly Dictionary<string, RuntimeRpcAppCallerSession> _active = new(StringComparer.Ordinal);
    private bool _disposed;
    private bool _observing;
    internal Func<RuntimePackageSource, CancellationToken, Task<IReadOnlyDictionary<string, SunderRpcContractDescriptor>>> LoadContracts { get; init; }
        = RuntimeRpcPackageMetadata.LoadContractsAsync;

    public async Task<RuntimeRpcAppSessionDescriptor> OpenAsync(
        RuntimeRpcAppSessionOpenRequest request,
        CancellationToken cancellationToken,
        AppPolicyPeer? peer = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_observing) { sessions.RpcSessionChanging += OnSessionChanging; _observing = true; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var targetDescriptor = request.Target;
        if (targetDescriptor is null
            || request.AppGenerationId == Guid.Empty
            || request.SessionGeneration < 0
            || !PackageId.TryParse(request.PackageId, out _)
            || !SemanticVersion.TryParse(request.PackageVersion, out _)
            || !RuntimeRpcPackageMetadata.IsSha256(request.ManifestSha256))
        {
            throw new RuntimeValidationException("The App RPC caller stamp is invalid.");
        }

        using var lease = sessions.AcquireLease();
        if (lease.Generation != request.SessionGeneration
            || !lease.Session.TryGetSessionPackage(request.PackageId, out var sessionPackage)
            || sessionPackage is null
            || !sessionPackage.IsEnabled
            || !lease.Session.TryGetPackageSource(request.PackageId, out var source)
            || source?.Manifest is null
            || source.ContentIndex is null)
        {
            throw new RuntimeStaleGenerationException(
                "The App RPC caller stamp does not identify an active package generation.");
        }
        var manifest = source.Manifest;
        var policySubject = PackageUiSnapshotStore.PolicySubject(source);
        AppPolicyActivationAuthority? authority = null;
        if (processPolicies is not null)
        {
            if (peer is null || policySubject is null) throw new RuntimeAppPolicyException(Sunder.Registry.Contracts.PackagePolicyErrorCodes.AppProcessSupervisionRequired);
            try { authority = processPolicies.RequireCallerPermit(peer, source, request.AppGenerationId, request.LoadPermitId, targetDescriptor); }
            catch (PackagePolicyDeniedException error) { throw new RuntimeAppPolicyException(error.Code); }
        }
        using var opening = authority?.Acquire();
        var manifestSha256 = RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex);
        if (!string.Equals(manifest.Id, request.PackageId, StringComparison.Ordinal)
            || !string.Equals(manifest.Version, request.PackageVersion, StringComparison.Ordinal)
            || !string.Equals(sessionPackage.Version, request.PackageVersion, StringComparison.Ordinal)
            || !string.Equals(manifestSha256, request.ManifestSha256, StringComparison.Ordinal))
        {
            throw new RuntimeStaleGenerationException(
                "The App RPC caller package identity or manifest fence is stale.");
        }

        if (!SunderPackageTargetKey.TryCreate(targetDescriptor.Role, targetDescriptor.Rid, out var targetKey)
             || !string.Equals(targetDescriptor.Role, SunderPackageFormat.AppHostRole, StringComparison.Ordinal)
             || !InstalledPackageProjection.Includes(source.AcquiredTargets, targetDescriptor.Role, targetDescriptor.Rid)
             || !SunderPackageTargetResolver.TryResolveTarget(manifest, targetKey, out var target)
             || target is null
             || target.Kind is not (SunderPackageFormat.WebTargetKind or SunderPackageFormat.AvaloniaTargetKind)
             || !RuntimeRpcPackageMetadata.TargetEquals(targetDescriptor, PackageTargetSelection.ToDescriptor(target)))
        {
            throw new RuntimeValidationException(
                "The App RPC caller stamp does not match an exact managed or web App target in the active package manifest.");
        }

        var contracts = await LoadContracts(
            source,
            cancellationToken).ConfigureAwait(false);
        var caller = new RuntimeRpcAppCallerSession(
            source,
            request.PackageId,
            request.PackageVersion,
            manifestSha256,
            (manifest.UsesContracts ?? []).Where(use => use is not null && InstalledPackageProjection.IncludesRole(source, use.Role!)).Select(static use => use!).ToArray(),
            contracts,
            request.SessionGeneration,
            request.AppGenerationId)
        {
            EnsureProcessPolicy = processPolicies is null ? null : () =>
            {
                try { processPolicies.RequireCallerPermit(peer!, source, request.AppGenerationId, request.LoadPermitId, targetDescriptor); }
                catch (PackagePolicyDeniedException error) { throw RuntimeRpcPolicyDenials.ToException(error); }
                catch (RuntimeUnavailableException error) { throw SunderRpcException.Infrastructure(SunderRpcErrorKind.Unavailable, "rpc.app-session.unavailable", error.Message); }
            },
        };
        caller.BindAuthority(authority);
        caller.BindSession(lease.Session);
        var sessionId = CreateSessionId();
        RuntimeRpcAppCallerSession[] retired = [];
        var capacityExceeded = false;
        bool opened;
        try
        {
            opened = sessions.ExecuteIfCurrent(lease, () =>
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (authority is not null && !authority.IsActive) throw new RuntimeStaleGenerationException("The App activation retired while its caller was opening.");
                    retired = _active
                        .Where(pair => !pair.Value.MatchesSession(lease.Session))
                        .Select(static pair => pair.Value)
                        .ToArray();
                    foreach (var stale in retired)
                    {
                        _active.Remove(stale.SessionId);
                    }
                    if (_active.Count >= policy.MaxAppCallerSessions)
                    {
                        capacityExceeded = true;
                    }
                    else
                    {
                        caller.SessionId = sessionId;
                        _active.Add(sessionId, caller);
                    }
                }
                return true;
            });
        }
        catch
        {
            caller.Retire();
            throw;
        }
        if (!opened)
        {
            caller.Retire();
            throw new RuntimeStaleGenerationException(
                "The App RPC caller stamp became stale while its contract metadata was loading.");
        }
        foreach (var stale in retired) stale.Retire();
        if (capacityExceeded)
        {
            caller.Retire();
            throw new RuntimeUnavailableException("The Runtime App RPC caller session limit was reached.");
        }
        return new RuntimeRpcAppSessionDescriptor(sessionId);
    }

    public void Close(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }
        RuntimeRpcAppCallerSession? caller;
        lock (_gate)
        {
            if (_disposed || !_active.Remove(sessionId, out caller))
            {
                return;
            }
        }
        caller.Retire();
    }

    public RuntimeRpcAppCallerSession GetRequired(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 64)
        {
            throw RetiredSession();
        }
        RuntimeRpcAppCallerSession caller;
        lock (_gate)
        {
            if (_disposed || !_active.TryGetValue(sessionId, out caller!)) throw RetiredSession();
        }
        // Policy/readiness validation can acquire PackageSessionState's monitor.
        // Opening commits in the opposite direction (package state -> dictionary),
        // so never execute that validation under the dictionary monitor.
        caller.EnsureProcessPolicy?.Invoke();
        lock (_gate)
        {
            // Close, disposal, generation replacement or authority retirement may
            // have won while validation ran. Broker acquisition also retains its
            // exact generation/activation fences after this lookup returns.
            if (!_disposed && _active.TryGetValue(sessionId, out var current)
                && ReferenceEquals(current, caller) && !caller.IsRetired) return caller;
        }
        throw RetiredSession();
    }

    private static SunderRpcException RetiredSession()
        => new(new SunderRpcError(
            SunderRpcErrorKind.Unavailable,
            "rpc.app-session.retired",
            "The App RPC caller session is unavailable or retired."));

    private void OnSessionChanging(ActivePackageSession successor)
    {
        RuntimeRpcAppCallerSession[] retired;
        lock (_gate)
        {
            retired = _active.Values.Where(caller => !caller.MatchesSession(successor)).ToArray();
            foreach (var caller in retired) _active.Remove(caller.SessionId);
        }
        foreach (var caller in retired) caller.Retire();
    }

    public void Dispose()
    {
        RuntimeRpcAppCallerSession[] active;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_observing) sessions.RpcSessionChanging -= OnSessionChanging;
            active = _active.Values.ToArray();
            _active.Clear();
        }
        foreach (var caller in active) caller.Retire();
    }

    private static string CreateSessionId()
        => "aw1_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

internal sealed class RuntimeRpcAppCallerSession : IRuntimeRpcCallerActivation
{
    private readonly object _gate = new();
    private readonly RuntimePackageSource _source;
    private readonly CancellationTokenSource _retirement = new();
    private int _callerCalls;
    private int _callerObservations;
    private bool _retired;
    private IDisposable? _authorityLease;
    private CancellationTokenRegistration _authorityRetirement;
    private string? _fingerprint;

    public RuntimeRpcAppCallerSession(
        RuntimePackageSource source,
        string packageId,
        string packageVersion,
        string manifestSha256,
        IReadOnlyList<SunderPackageContractUseManifest> contractUses,
        IReadOnlyDictionary<string, SunderRpcContractDescriptor> contracts,
        long sessionGeneration,
        Guid appGenerationId)
    {
        _source = source;
        PackageId = packageId;
        PackageVersion = packageVersion;
        ManifestSha256 = manifestSha256;
        RpcContractUses = contractUses.Where(use => PackageCompatibilityAuthority.DevelopmentUseEnabled(source, use.Role!, use.ContractId!, use.ContractVersion!)).ToArray();
        RpcContracts = contracts;
        SessionGeneration = sessionGeneration;
        AppGenerationId = appGenerationId;
    }

    public string SessionId { get; set; } = string.Empty;
    internal RuntimePackageSource Source => _source;
    internal Action? EnsureProcessPolicy { get; init; }
    internal AppPolicyActivationAuthority? Authority { get; private set; }
    internal bool IsRetired { get { lock (_gate) return _retired; } }

    internal void BindAuthority(AppPolicyActivationAuthority? authority)
    {
        if (authority is null) return;
        Authority = authority;
        _authorityLease = authority.Acquire();
        _authorityRetirement = authority.RetirementToken.UnsafeRegister(static state => ((RuntimeRpcAppCallerSession)state!).Retire(), this);
    }
    public string PackageId { get; }
    public string PackageVersion { get; }
    public Guid? ActivationId => AppGenerationId;
    public string Role => SunderPackageFormat.AppHostRole;
    public string ManifestSha256 { get; }
    public PackageSourceKind? SourceKind => _source.Kind;
    public IReadOnlyDictionary<string, SunderRpcContractDescriptor> RpcContracts { get; }
    public IReadOnlyList<SunderPackageContractUseManifest> RpcContractUses { get; }
    public long SessionGeneration { get; }
    public Guid AppGenerationId { get; }
    public CancellationToken RetirementToken => _retirement.Token;

    public bool IsGenerationCurrent(long generation)
        => !_retirement.IsCancellationRequested && SessionGeneration == generation;

    internal void BindSession(ActivePackageSession session)
        => _fingerprint = AppActivationFingerprint.Create(Guid.Empty, PackageId, session.GetActivePackageSources(), session.GetSessionPackages());

    internal bool MatchesSession(ActivePackageSession session)
        => !IsRetired && _fingerprint is not null
           && session.TryGetSessionPackage(PackageId, out var package) && package?.IsEnabled == true
           && package.Version == PackageVersion
           && session.TryGetPackageSource(PackageId, out var source) && source is not null
           && source.Kind == _source.Kind && PackageUiSnapshotStore.PolicySubject(source) == PackageUiSnapshotStore.PolicySubject(_source)
           && _fingerprint == AppActivationFingerprint.Create(Guid.Empty, PackageId, session.GetActivePackageSources(), session.GetSessionPackages());

    public bool IsCurrent(PackageSessionLease sessionLease)
    {
        EnsureProcessPolicy?.Invoke();
        return MatchesSession(sessionLease.Session);
    }

    public bool TryAcquireCaller(int maximumCalls, out RuntimeRpcActivationLease? lease, bool observation = false)
    {
        lock (_gate)
        {
            ref var active = ref (observation ? ref _callerObservations : ref _callerCalls);
            if (_retired || active >= maximumCalls)
            {
                lease = null;
                return false;
            }
            var operation = Authority?.Acquire();
            active++;
            lease = new RuntimeRpcActivationLease(_retirement.Token, () =>
            {
                try { ReleaseCaller(observation); }
                finally { operation?.Dispose(); }
            });
            return true;
        }
    }

    public void Retire()
    {
        lock (_gate)
        {
            if (_retired) return;
            _retired = true;
        }
        _ = CompleteRetirementAsync();
    }

    private async Task CompleteRetirementAsync()
    {
        await RuntimeCancellation.Signal(_retirement).ConfigureAwait(false);
        _authorityRetirement.Unregister();
        Interlocked.Exchange(ref _authorityLease, null)?.Dispose();
    }

    private void ReleaseCaller(bool observation)
    {
        lock (_gate)
        {
            ref var active = ref (observation ? ref _callerObservations : ref _callerCalls);
            if (active > 0) active--;
        }
    }
}

internal static class RuntimeRpcPackageMetadata
{
    public static string GetManifestSha256(SunderPackageContentIndex contentIndex)
        => contentIndex.Files?.SingleOrDefault(entry => string.Equals(
            entry?.Path,
            SunderPackageFormat.ManifestPath,
            StringComparison.Ordinal))?.Sha256
           ?? throw new InvalidDataException("Package content index is missing its manifest hash.");

    public static bool IsSha256(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static async Task<IReadOnlyDictionary<string, SunderRpcContractDescriptor>> LoadContractsAsync(
        RuntimePackageSource source,
        CancellationToken cancellationToken)
    {
        var manifest = source.Manifest ?? throw new InvalidDataException("Package source is missing its manifest.");
        PackageManifestAdmissionPolicy.EnsureSupported(manifest);
        var contentIndex = source.ContentIndex ?? throw new InvalidDataException("Package source is missing its content index.");
        var indexedFiles = (contentIndex.Files ?? []).Where(static entry => entry?.Path is not null)
            .ToDictionary(static entry => entry!.Path!, StringComparer.Ordinal);
        var contracts = new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal);
        foreach (var bundle in manifest.ContractBundles ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bundle is null
                || !ArchiveRelativePath.TryParse(
                    bundle.DescriptorPath,
                    SunderPackageFormat.MaxLogicalPathLength,
                    SunderPackageFormat.MaxLogicalPathDepth,
                    out var relativePath,
                    out _)
                || !ArchiveRelativePath.TryParse(
                    SunderPackageFormat.SharedPayloadRoot + relativePath,
                    SunderPackageFormat.MaxArchivePathLength,
                    SunderPackageFormat.MaxArchivePathDepth,
                    out var physicalPath,
                    out _)
                || !indexedFiles.TryGetValue(physicalPath.ToString(), out var indexed)
                || indexed is null)
            {
                throw new InvalidDataException("Package RPC contract metadata is invalid.");
            }
            var path = physicalPath.ToPlatformPath(source.EffectiveSnapshotFolder);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Package RPC contract descriptor '{relativePath}' is missing or linked.");
            }
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (indexed.Size != bytes.LongLength
                || !string.Equals(hash, indexed.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Package RPC contract descriptor '{relativePath}' failed its content-index integrity check.");
            }
            SunderRpcContractDescriptor descriptor;
            try
            {
                descriptor = SunderRpcContractDescriptor.Parse(bytes);
            }
            catch (SunderRpcDescriptorException exception)
            {
                throw new InvalidDataException(
                    $"Package RPC contract descriptor '{relativePath}' is invalid: {exception.Message}",
                    exception);
            }
            ValidateContractDescriptor(bundle, descriptor, relativePath.ToString());
            contracts.Add(PackageSessionPreparer.ContractKey(descriptor.ContractId, descriptor.Version), descriptor);
        }
        return contracts;
    }

    internal static void ValidateContractDescriptor(
        SunderPackageContractBundleManifest bundle,
        SunderRpcContractDescriptor descriptor,
        string descriptorPath)
    {
        if (descriptor.DescriptorVersion != SunderRpcProtocol.DescriptorVersion)
        {
            throw new InvalidDataException(
                $"Package RPC contract descriptor '{descriptorPath}' must use descriptorVersion {SunderRpcProtocol.DescriptorVersion}.");
        }
        if (!string.Equals(descriptor.ContractId, bundle.ContractId, StringComparison.Ordinal)
            || !string.Equals(descriptor.Version, bundle.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Package RPC contract descriptor '{descriptorPath}' failed its exact contract identity fence.");
        }
        if (!string.Equals(descriptor.Sha256, bundle.DescriptorSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Package RPC contract descriptor '{descriptorPath}' failed its descriptorSha256 fence.");
        }
        if (!string.Equals(descriptor.AbiSha256, bundle.AbiSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Package RPC contract descriptor '{descriptorPath}' failed its abiSha256 fence.");
        }
    }

    public static bool TargetEquals(PackageTargetDescriptor left, PackageTargetDescriptor right)
        => string.Equals(left.Role, right.Role, StringComparison.Ordinal)
           && string.Equals(left.Rid, right.Rid, StringComparison.Ordinal)
           && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
           && string.Equals(left.EntryPoint, right.EntryPoint, StringComparison.Ordinal)
           && string.Equals(left.TargetFramework, right.TargetFramework, StringComparison.Ordinal)
           && string.Equals(left.SdkVersion, right.SdkVersion, StringComparison.Ordinal)
           && left.RequiredHostCapabilities.SequenceEqual(right.RequiredHostCapabilities, StringComparer.Ordinal)
           && left.Views.Count == right.Views.Count
           && left.Views.Zip(right.Views).All(static pair => pair.First == pair.Second);
}
