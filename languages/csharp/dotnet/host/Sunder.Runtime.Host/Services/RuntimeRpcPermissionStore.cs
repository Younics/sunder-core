using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Format;
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
    private readonly Dictionary<RuntimeRpcPermissionKey, RuntimeRpcPermissionRecord> _records = [];
    private readonly Dictionary<RuntimeRpcPermissionKey, CancellationTokenSource> _effectiveLifetimes = [];
    private readonly Dictionary<RuntimeRpcPermissionKey, List<CancellationTokenSource>> _changes = [];
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

    internal RuntimeRpcPermissionLease ObserveChanges(RuntimeRpcPermissionKey key)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_changes.TryGetValue(key, out var changes))
            {
                changes = [];
                _changes.Add(key, changes);
            }
            var change = new CancellationTokenSource();
            changes.Add(change);
            return new RuntimeRpcPermissionLease(change.Token, () => ReleaseChangeObserver(key, change));
        }
    }

    private void ReleaseChangeObserver(RuntimeRpcPermissionKey key, CancellationTokenSource change)
    {
        lock (_gate)
        {
            if (!_changes.TryGetValue(key, out var observers) || !observers.Remove(change)) return;
            if (observers.Count == 0) _changes.Remove(key);
        }
        // A publisher takes ownership before signalling outside the lock. Only
        // the owner that removed this observer may dispose its source.
        change.Dispose();
    }

    public bool TryAcquire(RuntimeRpcPermissionKey key, out RuntimeRpcPermissionLease? lease)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_records.TryGetValue(key, out var record)
                || record.State != RuntimeRpcPermissionState.Granted)
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

    public bool IsGranted(RuntimeRpcPermissionKey key)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _records.TryGetValue(key, out var record)
                   && record.State == RuntimeRpcPermissionState.Granted;
        }
    }

    public async Task SetAsync(
        RuntimeRpcPermissionKey key,
        RuntimeRpcPermissionState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (state == RuntimeRpcPermissionState.Pending)
        {
            throw new ArgumentException("Pending is computed and cannot be persisted.", nameof(state));
        }
        if (!IsValidKey(key))
        {
            throw new ArgumentException("The RPC permission identity or fence is invalid.");
        }
        CancellationTokenSource? revoked = null;
        CancellationTokenSource[] changed = [];
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RuntimeRpcPermissionStateFile next;
            lock (_gate)
            {
                ThrowIfDisposed();
                var records = new Dictionary<RuntimeRpcPermissionKey, RuntimeRpcPermissionRecord>(_records)
                {
                    [key] = new RuntimeRpcPermissionRecord(
                        key,
                        state,
                        _timeProvider.GetUtcNow()),
                };
                next = CreateStateFile(checked(_revision + 1), records.Values);
            }

            Directory.CreateDirectory(_paths.CatalogRootPath);
            await DurableJsonDocument.WriteAsync(
                _paths.RpcPermissionFilePath,
                next,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                ThrowIfDisposed();
                var record = next.Permissions.Single(item => item.Key == key);
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
                changed = PublishChangeLocked([key]);
            }
        }
        finally
        {
            _writeGate.Release();
        }
        CancelAndDispose(revoked);
        foreach (var change in changed) CancelAndDispose(change);
    }

    public async Task GrantDeclaredInstalledActionsAsync(
        IEnumerable<RuntimeRpcPermissionSubject> packages,
        CancellationToken cancellationToken)
    {
        var installedPackages = packages
            .Where(static package => package.SourceKind == PackageSourceKind.Installed)
            .ToArray();
        foreach (var package in installedPackages)
        {
            if (!PackageId.TryParse(package.PackageId, out _)
                || !SemanticVersion.TryParse(package.PackageVersion, out _)
                || !IsHash(package.ManifestSha256))
            {
                throw new ArgumentException(
                    "The declared RPC permission package fence is invalid.",
                    nameof(packages));
            }
        }

        var now = _timeProvider.GetUtcNow();
        var grants = installedPackages
            .SelectMany(package => package.ContractUses.SelectMany(use =>
                (use.Actions ?? [])
                    .Where(static action => action is not null)
                    .Select(action => new RuntimeRpcPermissionRecord(
                        new RuntimeRpcPermissionKey(
                            package.PackageId,
                            package.PackageVersion,
                            package.ManifestSha256,
                            use.Role!,
                            use.ContractId!,
                            use.ContractVersion!,
                            use.AbiSha256!,
                            action!),
                        RuntimeRpcPermissionState.Granted,
                        now))))
            .GroupBy(static record => record.Key)
            .ToDictionary(static group => group.Key, static group => group.First());
        foreach (var record in grants.Values)
        {
            if (!IsValidKey(record.Key))
            {
                throw new ArgumentException(
                    "The declared RPC permission identity or fence is invalid.",
                    nameof(packages));
            }
        }

        var revoked = new List<CancellationTokenSource>();
        CancellationTokenSource[] changed = [];
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RuntimeRpcPermissionStateFile? next = null;
            RuntimeRpcPermissionKey[] addedKeys;
            RuntimeRpcPermissionKey[] removedKeys;
            lock (_gate)
            {
                ThrowIfDisposed();
                var activeFences = installedPackages
                    .GroupBy(static package => package.PackageId, StringComparer.Ordinal)
                    .ToDictionary(
                        static group => group.Key,
                        static group => (group.First().PackageVersion, group.First().ManifestSha256),
                        StringComparer.Ordinal);
                removedKeys = _records.Keys
                    .Where(key => activeFences.TryGetValue(key.CallerPackageId, out var fence)
                                  && (!string.Equals(key.CallerPackageVersion, fence.PackageVersion, StringComparison.Ordinal)
                                      || !string.Equals(key.ManifestSha256, fence.ManifestSha256, StringComparison.Ordinal)))
                    .ToArray();
                addedKeys = grants
                    .Where(pair => !_records.ContainsKey(pair.Key))
                    .Select(static pair => pair.Key)
                    .ToArray();
                if (addedKeys.Length > 0 || removedKeys.Length > 0)
                {
                    var records = new Dictionary<RuntimeRpcPermissionKey, RuntimeRpcPermissionRecord>(_records);
                    foreach (var key in removedKeys)
                    {
                        records.Remove(key);
                    }
                    foreach (var key in addedKeys)
                    {
                        records.Add(key, grants[key]);
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
            lock (_gate)
            {
                ThrowIfDisposed();
                foreach (var key in removedKeys)
                {
                    _records.Remove(key);
                    if (_effectiveLifetimes.Remove(key, out var previous))
                    {
                        revoked.Add(previous);
                    }
                }
                foreach (var key in addedKeys)
                {
                    _records[key] = grants[key];
                    if (_effectiveLifetimes.Remove(key, out var previous))
                    {
                        revoked.Add(previous);
                    }
                    _effectiveLifetimes[key] = new CancellationTokenSource();
                }
                _revision = next.Revision;
                changed = PublishChangeLocked(addedKeys.Concat(removedKeys));
            }
        }
        finally
        {
            _writeGate.Release();
        }
        foreach (var lifetime in revoked)
        {
            CancelAndDispose(lifetime);
        }
        foreach (var change in changed) CancelAndDispose(change);
    }

    public RuntimeRpcPermissionSnapshot GetSnapshot(IEnumerable<RuntimeRpcPermissionSubject> packages)
    {
        var packageSnapshot = packages.ToArray();
        lock (_gate)
        {
            ThrowIfDisposed();
            var output = new List<RuntimeRpcPermissionDescriptor>();
            var currentKeys = new HashSet<RuntimeRpcPermissionKey>();
            foreach (var package in packageSnapshot.OrderBy(
                         static package => package.PackageId,
                         StringComparer.Ordinal))
            {
                foreach (var use in package.ContractUses
                             .OrderBy(static use => use.Role, StringComparer.Ordinal)
                             .ThenBy(static use => use.ContractId, StringComparer.Ordinal))
                {
                    foreach (var action in (use.Actions ?? []).Where(static action => action is not null).Order(StringComparer.Ordinal))
                    {
                        var key = new RuntimeRpcPermissionKey(
                            package.PackageId,
                            package.PackageVersion,
                            package.ManifestSha256,
                            use.Role!,
                            use.ContractId!,
                            use.ContractVersion!,
                            use.AbiSha256!,
                            action!);
                        currentKeys.Add(key);
                        var hasExactRecord = _records.TryGetValue(key, out var record);
                        var developmentGrant = package.SourceKind == PackageSourceKind.Dev;
                        var state = developmentGrant
                            ? RuntimeRpcPermissionState.Granted
                            : hasExactRecord ? record!.State : RuntimeRpcPermissionState.Pending;
                        output.Add(new RuntimeRpcPermissionDescriptor(
                            key,
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
                    record.Key,
                    Requested: false,
                    record.State,
                    Effective: false,
                    record.UpdatedAtUtc));
            }
            return new RuntimeRpcPermissionSnapshot(
                _revision,
                output.OrderBy(static item => item.Key.CallerPackageId, StringComparer.Ordinal)
                    .ThenBy(static item => item.Key.CallerRole, StringComparer.Ordinal)
                    .ThenBy(static item => item.Key.ContractId, StringComparer.Ordinal)
                    .ThenBy(static item => item.Key.ContractVersion, StringComparer.Ordinal)
                    .ThenBy(static item => item.Key.AbiSha256, StringComparer.Ordinal)
                    .ThenBy(static item => item.Key.Action, StringComparer.Ordinal)
                    .ToArray());
        }
    }

    public void Dispose()
    {
        CancellationTokenSource[] lifetimes;
        CancellationTokenSource[] changes;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            lifetimes = _effectiveLifetimes.Values.ToArray();
            _effectiveLifetimes.Clear();
            changes = _changes.Values.SelectMany(observers => observers).ToArray();
            _changes.Clear();
        }
        foreach (var lifetime in lifetimes)
        {
            CancelAndDispose(lifetime);
        }
        foreach (var change in changes) CancelAndDispose(change);
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
        if (state?.SchemaVersion != 2 || state.Revision < 0 || state.Permissions is null)
        {
            throw new InvalidDataException("The Runtime RPC permission store has an unsupported schema.");
        }
        foreach (var record in state.Permissions)
        {
            if (record is null
                || record.Key is null
                || !IsValidKey(record.Key)
                || record.State == RuntimeRpcPermissionState.Pending)
            {
                throw new InvalidDataException("The Runtime RPC permission store contains an invalid permission.");
            }
            var key = record.Key;
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

    internal static bool IsValidKey(RuntimeRpcPermissionKey? key)
        => key is not null
           && PackageId.TryParse(key.CallerPackageId, out _)
           && SemanticVersion.TryParse(key.CallerPackageVersion, out _)
           && IsHash(key.ManifestSha256)
           && key.CallerRole is SunderPackageFormat.AppHostRole or SunderPackageFormat.RuntimeHostRole
           && PackageId.TryParse(key.ContractId, out _)
           && SemanticVersion.TryParse(key.ContractVersion, out _)
           && IsHash(key.AbiSha256)
           && IsAction(key.Action);

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
            2,
            revision,
            records.OrderBy(static item => item.Key.CallerPackageId, StringComparer.Ordinal)
                .ThenBy(static item => item.Key.CallerPackageVersion, StringComparer.Ordinal)
                .ThenBy(static item => item.Key.ManifestSha256, StringComparer.Ordinal)
                .ThenBy(static item => item.Key.CallerRole, StringComparer.Ordinal)
                .ThenBy(static item => item.Key.ContractId, StringComparer.Ordinal)
                .ThenBy(static item => item.Key.ContractVersion, StringComparer.Ordinal)
                .ThenBy(static item => item.Key.AbiSha256, StringComparer.Ordinal)
                .ThenBy(static item => item.Key.Action, StringComparer.Ordinal)
                .ToArray());

    private CancellationTokenSource[] PublishChangeLocked(IEnumerable<RuntimeRpcPermissionKey> keys)
    {
        var changed = new List<CancellationTokenSource>();
        foreach (var key in keys)
            if (_changes.Remove(key, out var previous)) changed.AddRange(previous);
        return changed.ToArray();
    }

}

internal sealed class RuntimeRpcPermissionLease(CancellationToken revocationToken, Action? release = null) : IDisposable
{
    private Action? _release = release;
    public CancellationToken RevocationToken { get; } = revocationToken;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

internal sealed record RuntimeRpcPermissionRecord(
    RuntimeRpcPermissionKey Key,
    RuntimeRpcPermissionState State,
    DateTimeOffset UpdatedAtUtc);

internal sealed record RuntimeRpcPermissionStateFile(
    int SchemaVersion,
    long Revision,
    IReadOnlyList<RuntimeRpcPermissionRecord> Permissions);

internal sealed class RuntimeRpcPermissionService(
    PackageSessionState sessions,
    RuntimeRpcPermissionStore store,
    RuntimeSessionOwner? owner = null)
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
        if (request?.Key is not { } requestedKey
            || !RuntimeRpcPermissionStore.IsValidKey(requestedKey))
        {
            throw new RuntimeValidationException("The RPC permission request is invalid.");
        }

        using (var lease = sessions.AcquireLease())
        {
            var subject = GetSubjects(lease).FirstOrDefault(package =>
                string.Equals(package.PackageId, requestedKey.CallerPackageId, StringComparison.OrdinalIgnoreCase))
                          ?? throw new RuntimeNotFoundException($"Package '{requestedKey.CallerPackageId}' is not active.");
            if (!string.Equals(subject.PackageVersion, requestedKey.CallerPackageVersion, StringComparison.Ordinal)
                || !string.Equals(subject.ManifestSha256, requestedKey.ManifestSha256, StringComparison.Ordinal))
            {
                throw new RuntimeValidationException(
                    $"The RPC permission request for package '{subject.PackageId}' is stale.");
            }
            var requestedUse = subject.ContractUses.FirstOrDefault(use =>
                string.Equals(use.Role, requestedKey.CallerRole, StringComparison.Ordinal)
                && string.Equals(use.ContractId, requestedKey.ContractId, StringComparison.Ordinal)
                && string.Equals(use.ContractVersion, requestedKey.ContractVersion, StringComparison.Ordinal)
                && string.Equals(use.AbiSha256, requestedKey.AbiSha256, StringComparison.Ordinal)
                && (use.Actions ?? []).Contains(requestedKey.Action, StringComparer.Ordinal));
            if (requestedUse is null)
            {
                throw new RuntimeValidationException(
                    $"Package '{subject.PackageId}' did not request the exact RPC permission '{requestedKey.Action}' for role '{requestedKey.CallerRole}' and contract '{requestedKey.ContractId}' version '{requestedKey.ContractVersion}'.");
            }
            if (subject.SourceKind == PackageSourceKind.Dev)
            {
                throw new RuntimeValidationException(
                    "Development package RPC access is activation-scoped and cannot be persisted.");
            }

            await store.SetAsync(
                new RuntimeRpcPermissionKey(
                    subject.PackageId,
                    subject.PackageVersion,
                    subject.ManifestSha256,
                    requestedUse.Role!,
                    requestedUse.ContractId!,
                    requestedUse.ContractVersion!,
                    requestedUse.AbiSha256!,
                    requestedKey.Action),
                state,
                cancellationToken).ConfigureAwait(false);
        }
        owner?.RefreshRpcReadiness();
        return GetSnapshot();
    }

    private static IReadOnlyList<RuntimeRpcPermissionSubject> GetSubjects(PackageSessionLease lease)
        => RuntimeRpcPermissionSubjects.FromSession(lease.Session);
}
