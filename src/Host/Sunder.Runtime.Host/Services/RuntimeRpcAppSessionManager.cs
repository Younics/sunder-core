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
    string ManifestSha256 { get; }
    PackageSourceKind? SourceKind { get; }
    IReadOnlyDictionary<string, SunderRpcContractDescriptor> RpcContracts { get; }
    IReadOnlyList<SunderPackageContractUseManifest> RpcContractUses { get; }
    CancellationToken RetirementToken { get; }
    bool IsCurrent(PackageSessionLease sessionLease);
    bool TryAcquireCaller(int maximumCalls, out RuntimeRpcActivationLease? lease);
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
            .Select(static source => new RuntimeRpcPermissionSubject(
                source.PackageId,
                source.Manifest!.Version!,
                RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex!),
                source.Kind,
                (source.Manifest.UsesContracts ?? [])
                    .Where(static use => use is not null)
                    .Select(static use => use!)
                    .ToArray()))
            .ToArray();
}

internal sealed class RuntimeRpcAppSessionManager(
    PackageSessionState sessions,
    RuntimeRpcPolicyOptions policy) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RuntimeRpcAppCallerSession> _active = new(StringComparer.Ordinal);
    private bool _disposed;

    public async Task<RuntimeRpcAppSessionDescriptor> OpenAsync(
        RuntimeRpcAppSessionOpenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
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
             || !SunderPackageTargetResolver.TryResolveTarget(manifest, targetKey, out var target)
             || target is null
             || target.Kind is not (SunderPackageFormat.WebTargetKind or SunderPackageFormat.AvaloniaTargetKind)
             || !RuntimeRpcPackageMetadata.TargetEquals(targetDescriptor, PackageTargetSelection.ToDescriptor(target)))
        {
            throw new RuntimeValidationException(
                "The App RPC caller stamp does not match an exact managed or web App target in the active package manifest.");
        }

        var contracts = await RuntimeRpcPackageMetadata.LoadContractsAsync(
            source,
            cancellationToken).ConfigureAwait(false);
        var caller = new RuntimeRpcAppCallerSession(
            source,
            request.PackageId,
            request.PackageVersion,
            manifestSha256,
            (manifest.UsesContracts ?? []).Where(static use => use is not null).Select(static use => use!).ToArray(),
            contracts,
            request.SessionGeneration,
            request.AppGenerationId);
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
                    retired = _active
                        .Where(pair => !pair.Value.IsGenerationCurrent(request.SessionGeneration))
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
        lock (_gate)
        {
            if (!_disposed && _active.TryGetValue(sessionId, out var caller))
            {
                return caller;
            }
        }
        throw RetiredSession();
    }

    private static SunderRpcException RetiredSession()
        => new(new SunderRpcError(
            SunderRpcErrorKind.Unavailable,
            "rpc.app-session.retired",
            "The App RPC caller session is unavailable or retired."));

    public void Dispose()
    {
        RuntimeRpcAppCallerSession[] active;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
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
    private bool _retired;

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
        RpcContractUses = contractUses;
        RpcContracts = contracts;
        SessionGeneration = sessionGeneration;
        AppGenerationId = appGenerationId;
    }

    public string SessionId { get; set; } = string.Empty;
    public string PackageId { get; }
    public string PackageVersion { get; }
    public string ManifestSha256 { get; }
    public PackageSourceKind? SourceKind => _source.Kind;
    public IReadOnlyDictionary<string, SunderRpcContractDescriptor> RpcContracts { get; }
    public IReadOnlyList<SunderPackageContractUseManifest> RpcContractUses { get; }
    public long SessionGeneration { get; }
    public Guid AppGenerationId { get; }
    public CancellationToken RetirementToken => _retirement.Token;

    public bool IsGenerationCurrent(long generation)
        => !_retirement.IsCancellationRequested && SessionGeneration == generation;

    public bool IsCurrent(PackageSessionLease sessionLease)
    {
        lock (_gate)
        {
            return !_retired
                   && sessionLease.Generation == SessionGeneration
                   && sessionLease.Session.TryGetSessionPackage(PackageId, out var package)
                   && package?.IsEnabled == true
                   && string.Equals(package.Version, PackageVersion, StringComparison.Ordinal)
                   && sessionLease.Session.TryGetPackageSource(PackageId, out var source)
                   && ReferenceEquals(source, _source);
        }
    }

    public bool TryAcquireCaller(int maximumCalls, out RuntimeRpcActivationLease? lease)
    {
        lock (_gate)
        {
            if (_retired || _callerCalls >= maximumCalls)
            {
                lease = null;
                return false;
            }
            _callerCalls++;
            lease = new RuntimeRpcActivationLease(_retirement.Token, ReleaseCaller);
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
        _ = RuntimeCancellation.Signal(_retirement);
    }

    private void ReleaseCaller()
    {
        lock (_gate)
        {
            if (_callerCalls > 0) _callerCalls--;
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
                || !string.Equals(indexed.Sha256, bundle.Sha256, StringComparison.Ordinal))
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
            var descriptor = SunderRpcContractDescriptor.Parse(bytes);
            if (!string.Equals(hash, bundle.Sha256, StringComparison.Ordinal)
                || !string.Equals(descriptor.ContractId, bundle.ContractId, StringComparison.Ordinal)
                || !string.Equals(descriptor.Version, bundle.Version, StringComparison.Ordinal)
                || !string.Equals(descriptor.Sha256, bundle.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Package RPC contract descriptor '{relativePath}' failed its identity fence.");
            }
            contracts.Add(PackageSessionPreparer.ContractKey(descriptor.ContractId, descriptor.Version), descriptor);
        }
        return contracts;
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
