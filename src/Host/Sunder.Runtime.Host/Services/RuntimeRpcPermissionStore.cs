using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeRpcPermissionStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };
    private readonly RuntimePackagePaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<PermissionKey, RuntimeRpcPermissionRecord> _records = [];
    private readonly Dictionary<PermissionKey, CancellationTokenSource> _effectiveLifetimes = [];
    private CancellationTokenSource _changes = new();
    private long _revision;
    private bool _disposed;

    public RuntimeRpcPermissionStore(RuntimePackagePaths paths, TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Load();
    }

    public long Revision
    {
        get
        {
            lock (_gate) return _revision;
        }
    }

    public CancellationToken ChangeToken
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _changes.Token;
            }
        }
    }

    public bool TryAcquire(
        string callerPackageId,
        string callerPackageVersion,
        string manifestSha256,
        string contractId,
        string action,
        out RuntimeRpcPermissionLease? lease)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var key = new PermissionKey(callerPackageId, contractId, action);
            if (!_records.TryGetValue(key, out var record)
                || record.State != RuntimeRpcPermissionState.Granted
                || !string.Equals(record.CallerPackageVersion, callerPackageVersion, StringComparison.Ordinal)
                || !string.Equals(record.ManifestSha256, manifestSha256, StringComparison.Ordinal))
            {
                lease = null;
                return false;
            }
            if (!_effectiveLifetimes.TryGetValue(key, out var lifetime))
            {
                lifetime = new CancellationTokenSource();
                _effectiveLifetimes.Add(key, lifetime);
            }
            lease = new RuntimeRpcPermissionLease(lifetime.Token);
            return true;
        }
    }

    public async Task SetAsync(
        string callerPackageId,
        string callerPackageVersion,
        string manifestSha256,
        string contractId,
        string action,
        RuntimeRpcPermissionState state,
        CancellationToken cancellationToken)
    {
        if (state == RuntimeRpcPermissionState.Pending)
        {
            throw new ArgumentException("Pending is computed and cannot be persisted.", nameof(state));
        }
        if (!PackageId.TryParse(callerPackageId, out _)
            || !SemanticVersion.TryParse(callerPackageVersion, out _)
            || !IsHash(manifestSha256)
            || !PackageId.TryParse(contractId, out _)
            || !IsAction(action))
        {
            throw new ArgumentException("The RPC permission identity or fence is invalid.");
        }
        var key = new PermissionKey(callerPackageId, contractId, action);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RuntimeRpcPermissionStateFile next;
            lock (_gate)
            {
                ThrowIfDisposed();
                var records = new Dictionary<PermissionKey, RuntimeRpcPermissionRecord>(_records)
                {
                    [key] = new RuntimeRpcPermissionRecord(
                        callerPackageId,
                        callerPackageVersion,
                        manifestSha256,
                        contractId,
                        action,
                        state,
                        _timeProvider.GetUtcNow()),
                };
                next = new RuntimeRpcPermissionStateFile(
                    1,
                    checked(_revision + 1),
                    records.Values.OrderBy(static item => item.CallerPackageId, StringComparer.Ordinal)
                        .ThenBy(static item => item.ContractId, StringComparer.Ordinal)
                        .ThenBy(static item => item.Action, StringComparer.Ordinal)
                        .ToArray());
            }

            Directory.CreateDirectory(_paths.CatalogRootPath);
            await DurableJsonDocument.WriteAsync(
                _paths.RpcPermissionFilePath,
                next,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            CancellationTokenSource? revoked = null;
            CancellationTokenSource changed;
            lock (_gate)
            {
                ThrowIfDisposed();
                var record = next.Permissions.Single(item =>
                    string.Equals(item.CallerPackageId, callerPackageId, StringComparison.Ordinal)
                    && string.Equals(item.ContractId, contractId, StringComparison.Ordinal)
                    && string.Equals(item.Action, action, StringComparison.Ordinal));
                _records[key] = record;
                _revision = next.Revision;
                if (state == RuntimeRpcPermissionState.Granted)
                {
                    if (_effectiveLifetimes.Remove(key, out var previous)) revoked = previous;
                    _effectiveLifetimes[key] = new CancellationTokenSource();
                }
                else
                {
                    _effectiveLifetimes.Remove(key, out revoked);
                }
                changed = PublishChangeLocked();
            }
            CancelAndDispose(revoked);
            CancelAndDispose(changed);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task GrantDeclaredInstalledActionsAsync(
        IEnumerable<RuntimeRpcPermissionSubject> packages,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var grants = packages
            .Where(static package => package.SourceKind == PackageSourceKind.Installed)
            .SelectMany(package => package.ContractUses.SelectMany(use =>
                (use.Actions ?? [])
                    .Where(static action => action is not null)
                    .Select(action => new RuntimeRpcPermissionRecord(
                        package.PackageId,
                        package.PackageVersion,
                        package.ManifestSha256,
                        use.ContractId!,
                        action!,
                        RuntimeRpcPermissionState.Granted,
                        now))))
            .ToDictionary(
                static record => new PermissionKey(record.CallerPackageId, record.ContractId, record.Action),
                static record => record);
        if (grants.Count == 0)
        {
            return;
        }
        foreach (var record in grants.Values)
        {
            if (!PackageId.TryParse(record.CallerPackageId, out _)
                || !SemanticVersion.TryParse(record.CallerPackageVersion, out _)
                || !IsHash(record.ManifestSha256)
                || !PackageId.TryParse(record.ContractId, out _)
                || !IsAction(record.Action))
            {
                throw new ArgumentException(
                    "The declared RPC permission identity or fence is invalid.",
                    nameof(packages));
            }
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RuntimeRpcPermissionStateFile? next = null;
            PermissionKey[] changedKeys;
            lock (_gate)
            {
                ThrowIfDisposed();
                changedKeys = grants
                    .Where(pair => !_records.TryGetValue(pair.Key, out var current)
                                   || !string.Equals(
                                       current.CallerPackageVersion,
                                       pair.Value.CallerPackageVersion,
                                       StringComparison.Ordinal)
                                   || !string.Equals(
                                       current.ManifestSha256,
                                       pair.Value.ManifestSha256,
                                       StringComparison.Ordinal))
                    .Select(static pair => pair.Key)
                    .ToArray();
                if (changedKeys.Length > 0)
                {
                    var records = new Dictionary<PermissionKey, RuntimeRpcPermissionRecord>(_records);
                    foreach (var key in changedKeys)
                    {
                        records[key] = grants[key];
                    }
                    next = CreateStateFile(checked(_revision + 1), records.Values);
                }
            }
            if (next is null)
            {
                return;
            }

            Directory.CreateDirectory(_paths.CatalogRootPath);
            await DurableJsonDocument.WriteAsync(
                _paths.RpcPermissionFilePath,
                next,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            var revoked = new List<CancellationTokenSource>();
            CancellationTokenSource changed;
            lock (_gate)
            {
                ThrowIfDisposed();
                foreach (var key in changedKeys)
                {
                    _records[key] = grants[key];
                    if (_effectiveLifetimes.Remove(key, out var previous))
                    {
                        revoked.Add(previous);
                    }
                    _effectiveLifetimes[key] = new CancellationTokenSource();
                }
                _revision = next.Revision;
                changed = PublishChangeLocked();
            }
            foreach (var lifetime in revoked)
            {
                CancelAndDispose(lifetime);
            }
            CancelAndDispose(changed);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public RuntimeRpcPermissionSnapshot GetSnapshot(IEnumerable<RuntimeRpcPermissionSubject> packages)
    {
        var packageSnapshot = packages.ToArray();
        lock (_gate)
        {
            ThrowIfDisposed();
            var output = new List<RuntimeRpcPermissionDescriptor>();
            var currentKeys = new HashSet<PermissionKey>();
            foreach (var package in packageSnapshot.OrderBy(
                         static package => package.PackageId,
                         StringComparer.Ordinal))
            {
                foreach (var use in package.ContractUses.OrderBy(static use => use.ContractId, StringComparer.Ordinal))
                {
                    foreach (var action in (use.Actions ?? []).Where(static action => action is not null).Order(StringComparer.Ordinal))
                    {
                        var key = new PermissionKey(package.PackageId, use.ContractId!, action!);
                        currentKeys.Add(key);
                        var hasExactRecord = _records.TryGetValue(key, out var record)
                                              && string.Equals(record.CallerPackageVersion, package.PackageVersion, StringComparison.Ordinal)
                                              && string.Equals(record.ManifestSha256, package.ManifestSha256, StringComparison.Ordinal);
                        var developmentGrant = package.SourceKind == PackageSourceKind.Dev;
                        var state = developmentGrant
                            ? RuntimeRpcPermissionState.Granted
                            : hasExactRecord ? record!.State : RuntimeRpcPermissionState.Pending;
                        output.Add(new RuntimeRpcPermissionDescriptor(
                            package.PackageId,
                            package.PackageVersion,
                            package.ManifestSha256,
                            use.ContractId!,
                            action!,
                            Requested: true,
                            state,
                            Effective: state == RuntimeRpcPermissionState.Granted,
                            developmentGrant ? null : hasExactRecord ? record!.UpdatedAtUtc : null));
                    }
                }
            }
            foreach (var (key, record) in _records.Where(pair => !currentKeys.Contains(pair.Key)))
            {
                output.Add(new RuntimeRpcPermissionDescriptor(
                    record.CallerPackageId,
                    record.CallerPackageVersion,
                    record.ManifestSha256,
                    record.ContractId,
                    record.Action,
                    Requested: false,
                    record.State,
                    Effective: false,
                    record.UpdatedAtUtc));
            }
            return new RuntimeRpcPermissionSnapshot(
                _revision,
                output.OrderBy(static item => item.CallerPackageId, StringComparer.Ordinal)
                    .ThenBy(static item => item.ContractId, StringComparer.Ordinal)
                    .ThenBy(static item => item.Action, StringComparer.Ordinal)
                    .ToArray());
        }
    }

    public void Dispose()
    {
        CancellationTokenSource[] lifetimes;
        CancellationTokenSource changes;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            lifetimes = _effectiveLifetimes.Values.ToArray();
            _effectiveLifetimes.Clear();
            changes = _changes;
        }
        foreach (var lifetime in lifetimes)
        {
            CancelAndDispose(lifetime);
        }
        CancelAndDispose(changes);
        _writeGate.Dispose();
    }

    private void Load()
    {
        if (!File.Exists(_paths.RpcPermissionFilePath)) return;
        RuntimeRpcPermissionStateFile? state;
        try
        {
            state = JsonSerializer.Deserialize<RuntimeRpcPermissionStateFile>(
                File.ReadAllText(_paths.RpcPermissionFilePath),
                JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Runtime RPC permission store is invalid.", exception);
        }
        if (state?.SchemaVersion != 1 || state.Revision < 0 || state.Permissions is null)
        {
            throw new InvalidDataException("The Runtime RPC permission store has an unsupported schema.");
        }
        foreach (var record in state.Permissions)
        {
            if (record is null
                || !PackageId.TryParse(record.CallerPackageId, out _)
                || !SemanticVersion.TryParse(record.CallerPackageVersion, out _)
                || !PackageId.TryParse(record.ContractId, out _)
                || !IsAction(record.Action)
                || record.State == RuntimeRpcPermissionState.Pending
                || !IsHash(record.ManifestSha256))
            {
                throw new InvalidDataException("The Runtime RPC permission store contains an invalid permission.");
            }
            var key = new PermissionKey(record.CallerPackageId, record.ContractId, record.Action);
            if (!_records.TryAdd(key, record))
            {
                throw new InvalidDataException("The Runtime RPC permission store contains a duplicate permission.");
            }
            if (record.State == RuntimeRpcPermissionState.Granted)
            {
                _effectiveLifetimes.Add(key, new CancellationTokenSource());
            }
        }
        _revision = state.Revision;
    }

    internal static bool IsAction(string? action)
        => action is SunderRpcProtocol.DiscoverAction
            or SunderRpcProtocol.InvokeAction
            or SunderRpcProtocol.SubscribeAction;

    private static bool IsHash(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }
        try
        {
            cancellation.Cancel(throwOnFirstException: false);
        }
        catch (AggregateException)
        {
            // Permission state is already committed; a consumer callback cannot roll it back.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static RuntimeRpcPermissionStateFile CreateStateFile(
        long revision,
        IEnumerable<RuntimeRpcPermissionRecord> records)
        => new(
            1,
            revision,
            records.OrderBy(static item => item.CallerPackageId, StringComparer.Ordinal)
                .ThenBy(static item => item.ContractId, StringComparer.Ordinal)
                .ThenBy(static item => item.Action, StringComparer.Ordinal)
                .ToArray());

    private CancellationTokenSource PublishChangeLocked()
    {
        var previous = _changes;
        _changes = new CancellationTokenSource();
        return previous;
    }

    private readonly record struct PermissionKey(string CallerPackageId, string ContractId, string Action);
}

internal sealed class RuntimeRpcPermissionLease(CancellationToken revocationToken) : IDisposable
{
    public CancellationToken RevocationToken { get; } = revocationToken;

    public void Dispose()
    {
    }
}

internal sealed record RuntimeRpcPermissionRecord(
    string CallerPackageId,
    string CallerPackageVersion,
    string ManifestSha256,
    string ContractId,
    string Action,
    RuntimeRpcPermissionState State,
    DateTimeOffset UpdatedAtUtc);

internal sealed record RuntimeRpcPermissionStateFile(
    int SchemaVersion,
    long Revision,
    IReadOnlyList<RuntimeRpcPermissionRecord> Permissions);

internal sealed class RuntimeRpcPermissionService(
    PackageSessionState sessions,
    RuntimeRpcPermissionStore store)
{
    public RuntimeRpcPermissionSnapshot GetSnapshot()
    {
        using var lease = sessions.AcquireLease();
        return store.GetSnapshot(GetSubjects(lease));
    }

    public async Task<RuntimeRpcPermissionSnapshot> SetAsync(
        RuntimeRpcPermissionUpdateRequest request,
        RuntimeRpcPermissionState state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerPackageId)
            || string.IsNullOrWhiteSpace(request.ContractId)
            || !RuntimeRpcPermissionStore.IsAction(request.Action))
        {
            throw new RuntimeValidationException("The RPC permission request is invalid.");
        }

        using (var lease = sessions.AcquireLease())
        {
            var subject = GetSubjects(lease).FirstOrDefault(package =>
                string.Equals(package.PackageId, request.CallerPackageId, StringComparison.OrdinalIgnoreCase))
                          ?? throw new RuntimeNotFoundException($"Package '{request.CallerPackageId}' is not active.");
            var requestedUse = subject.ContractUses.FirstOrDefault(use =>
                string.Equals(use.ContractId, request.ContractId, StringComparison.Ordinal)
                && (use.Actions ?? []).Contains(request.Action, StringComparer.Ordinal));
            if (requestedUse is null)
            {
                throw new RuntimeValidationException(
                    $"Package '{subject.PackageId}' did not request RPC action '{request.Action}' for contract '{request.ContractId}'.");
            }
            if (subject.SourceKind == PackageSourceKind.Dev)
            {
                throw new RuntimeValidationException(
                    "Development package RPC access is activation-scoped and cannot be persisted.");
            }

            await store.SetAsync(
                subject.PackageId,
                subject.PackageVersion,
                subject.ManifestSha256,
                requestedUse.ContractId!,
                request.Action,
                state,
                cancellationToken).ConfigureAwait(false);
        }
        return GetSnapshot();
    }

    private static IReadOnlyList<RuntimeRpcPermissionSubject> GetSubjects(PackageSessionLease lease)
        => RuntimeRpcPermissionSubjects.FromSession(lease.Session);
}
