using System.Diagnostics;
using System.Net;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public interface IDevPackageOwnerSession : IAsyncDisposable
{
    Task<DevPackageOwnerLeaseResponse> AcquireAsync(
        IReadOnlyList<string> folders,
        bool watch,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(CancellationToken cancellationToken = default);
}

public sealed class DevPackageOwnerSessionOptions
{
    public TimeSpan ReleaseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan MinimumHeartbeatDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaximumHeartbeatDelay { get; init; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (ReleaseTimeout <= TimeSpan.Zero || ReleaseTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(ReleaseTimeout), "The owner release timeout must be bounded and positive.");
        }
        if (MinimumHeartbeatDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumHeartbeatDelay));
        }
        if (MaximumHeartbeatDelay < MinimumHeartbeatDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumHeartbeatDelay));
        }
    }
}

internal interface IDevPackageOwnerClient : IDisposable
{
    Task<RuntimeHandshakeResponse> GetRuntimeHandshakeAsync(CancellationToken cancellationToken = default);

    Task<DevPackageOwnerLeaseResponse> ReplaceDevPackageOwnerAsync(
        string ownerId,
        DevPackageOwnerMutationRequest request,
        CancellationToken cancellationToken = default);

    Task<DevPackageOwnerLeaseResponse> HeartbeatDevPackageOwnerAsync(
        string ownerId,
        DevPackageOwnerHeartbeatRequest request,
        CancellationToken cancellationToken = default);

    Task ReleaseDevPackageOwnerAsync(
        string ownerId,
        DevPackageOwnerReleaseRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class DevPackageOwnerSession : IDevPackageOwnerSession
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly Func<IDevPackageOwnerClient> _clients;
    private readonly Func<RuntimeConnectionInfo?> _getConnectionInfo;
    private readonly Action<string, Exception>? _reportFault;
    private readonly DevPackageOwnerSessionOptions _options;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly object _syncRoot = new();
    private readonly int _processId = Environment.ProcessId;
    private readonly DateTimeOffset _processStartedAtUtc;
    private DevPackageOwnerLeaseResponse? _lease;
    private DevPackageOwnerMutationRequest? _pendingMutation;
    private IReadOnlyList<DevPackageOwnerFolder>? _desiredFolders;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task _heartbeatTask = Task.CompletedTask;
    private bool _releasing;
    private int _disposed;

    public DevPackageOwnerSession(
        RuntimeClientTransport transport,
        string ownerIdPrefix,
        Action<string, Exception>? reportFault = null,
        DevPackageOwnerSessionOptions? options = null)
        : this(
            () => new RuntimeManagementClient(transport),
            transport.GetConnectionInfo,
            ownerIdPrefix,
            reportFault,
            options)
    {
    }

    internal DevPackageOwnerSession(
        Func<IDevPackageOwnerClient> clients,
        Func<RuntimeConnectionInfo?> getConnectionInfo,
        string ownerIdPrefix,
        Action<string, Exception>? reportFault = null,
        DevPackageOwnerSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(getConnectionInfo);
        ValidateOwnerIdPrefix(ownerIdPrefix);

        _clients = clients;
        _getConnectionInfo = getConnectionInfo;
        _reportFault = reportFault;
        _options = options ?? new DevPackageOwnerSessionOptions();
        _options.Validate();
        OwnerId = $"{ownerIdPrefix}-{Guid.NewGuid():N}";
        OwnerToken = CreateSeparatedOwnerToken();

        using var process = Process.GetCurrentProcess();
        _processStartedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
    }

    internal string OwnerId { get; }

    internal string OwnerToken { get; private set; }

    public async Task<DevPackageOwnerLeaseResponse> AcquireAsync(
        IReadOnlyList<string> folders,
        bool watch,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(folders);
        var desiredFolders = folders
            .Select(folder => new DevPackageOwnerFolder(
                Path.GetFullPath(folder ?? throw new ArgumentException("A development package folder cannot be null.", nameof(folders))),
                watch))
            .OrderBy(static folder => folder.Folder, PathComparer)
            .ThenBy(static folder => folder.Watch)
            .ToArray();

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var client = _clients();
            var handshake = await client.GetRuntimeHandshakeAsync(cancellationToken).ConfigureAwait(false);
            ResetStaleRuntimeState(handshake.RuntimeInstanceId);

            var current = await ResolvePendingMutationAsync(
                client,
                handshake.RuntimeInstanceId,
                cancellationToken).ConfigureAwait(false);
            if (current is not null && FoldersEqual(GetDesiredFolders(), desiredFolders))
            {
                EnsureHeartbeatStarted();
                return current;
            }

            current = GetLease(handshake.RuntimeInstanceId);
            EnsureOwnerTokenSeparatedWhenUnowned();
            var request = CreateMutation(
                handshake.RuntimeInstanceId,
                current is null ? 1 : checked(current.Revision + 1),
                desiredFolders);
            SetPendingMutation(request);

            DevPackageOwnerLeaseResponse lease;
            try
            {
                lease = await client.ReplaceDevPackageOwnerAsync(
                    OwnerId,
                    request,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (RuntimeClientException exception)
                when (exception.StatusCode == HttpStatusCode.Unauthorized && current is not null)
            {
                ClearOwnershipState(handshake.RuntimeInstanceId);
                EnsureOwnerTokenSeparatedWhenUnowned();
                request = CreateMutation(handshake.RuntimeInstanceId, 1, desiredFolders);
                SetPendingMutation(request);
                lease = await client.ReplaceDevPackageOwnerAsync(
                    OwnerId,
                    request,
                    cancellationToken).ConfigureAwait(false);
            }

            CommitMutation(request, lease);
            EnsureHeartbeatStarted();
            return lease;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(_options.ReleaseTimeout);
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await ReleaseCoreAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Development package owner release exceeded the {_options.ReleaseTimeout} cleanup budget.",
                exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportFault("release", exception);
        }
    }

    private async Task<DevPackageOwnerLeaseResponse?> ResolvePendingMutationAsync(
        IDevPackageOwnerClient client,
        Guid runtimeInstanceId,
        CancellationToken cancellationToken)
    {
        DevPackageOwnerMutationRequest? pending;
        lock (_syncRoot)
        {
            pending = _pendingMutation?.RuntimeInstanceId == runtimeInstanceId
                ? _pendingMutation
                : null;
        }
        if (pending is null)
        {
            return GetLease(runtimeInstanceId);
        }

        try
        {
            var resolved = await client.ReplaceDevPackageOwnerAsync(
                OwnerId,
                pending,
                cancellationToken).ConfigureAwait(false);
            CommitMutation(pending, resolved);
            return resolved;
        }
        catch (RuntimeClientException exception)
            when (exception.StatusCode == HttpStatusCode.Unauthorized && pending.Revision > 1)
        {
            ClearOwnershipState(runtimeInstanceId);
            return null;
        }
    }

    private async Task ReleaseCoreAsync(CancellationToken cancellationToken)
    {
        SignalHeartbeatStop();
        var enteredMutationGate = false;
        try
        {
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredMutationGate = true;
            await StopHeartbeatAsync(cancellationToken).ConfigureAwait(false);

            DevPackageOwnerLeaseResponse? lease;
            DevPackageOwnerMutationRequest? pending;
            lock (_syncRoot)
            {
                lease = _lease;
                pending = _pendingMutation;
            }
            var runtimeInstanceId = pending?.RuntimeInstanceId ?? lease?.RuntimeInstanceId;
            if (runtimeInstanceId is null)
            {
                ClearOwnershipState();
                return;
            }

            using var client = _clients();
            try
            {
                await client.ReleaseDevPackageOwnerAsync(
                    OwnerId,
                    new DevPackageOwnerReleaseRequest(runtimeInstanceId.Value, OwnerToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (RuntimeClientException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                // A stale Runtime instance cannot retain an owner in the replacement Runtime.
            }

            lock (_syncRoot)
            {
                if (_lease == lease && _pendingMutation == pending)
                {
                    _lease = null;
                    _pendingMutation = null;
                    _desiredFolders = null;
                }
            }
        }
        finally
        {
            lock (_syncRoot)
            {
                _releasing = false;
            }
            if (enteredMutationGate)
            {
                _mutationGate.Release();
            }
        }
    }

    private void SignalHeartbeatStop()
    {
        lock (_syncRoot)
        {
            _releasing = true;
            _heartbeatCancellation?.Cancel();
        }
    }

    private async Task StopHeartbeatAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? heartbeatCancellation;
        Task heartbeatTask;
        lock (_syncRoot)
        {
            _releasing = true;
            heartbeatCancellation = _heartbeatCancellation;
            heartbeatTask = _heartbeatTask;
            heartbeatCancellation?.Cancel();
        }

        await heartbeatTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_syncRoot)
        {
            if (ReferenceEquals(_heartbeatCancellation, heartbeatCancellation)
                && ReferenceEquals(_heartbeatTask, heartbeatTask))
            {
                _heartbeatCancellation = null;
                _heartbeatTask = Task.CompletedTask;
                heartbeatCancellation?.Dispose();
            }
        }
    }

    private async Task RunHeartbeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DevPackageOwnerLeaseResponse? lease;
            bool releasing;
            lock (_syncRoot)
            {
                lease = _lease;
                releasing = _releasing;
            }
            if (releasing)
            {
                return;
            }
            if (lease is null)
            {
                try
                {
                    await Task.Delay(_options.MinimumHeartbeatDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                continue;
            }

            var remaining = lease.ExpiresAtUtc - DateTimeOffset.UtcNow;
            var milliseconds = Math.Clamp(
                remaining.TotalMilliseconds / 3,
                _options.MinimumHeartbeatDelay.TotalMilliseconds,
                _options.MaximumHeartbeatDelay.TotalMilliseconds);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).ConfigureAwait(false);
                using var client = _clients();
                var handshake = await client.GetRuntimeHandshakeAsync(cancellationToken).ConfigureAwait(false);
                if (handshake.RuntimeInstanceId != lease.RuntimeInstanceId)
                {
                    await ReacquireAsync(client, lease, handshake.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                DevPackageOwnerLeaseResponse renewed;
                try
                {
                    renewed = await client.HeartbeatDevPackageOwnerAsync(
                        OwnerId,
                        new DevPackageOwnerHeartbeatRequest(lease.RuntimeInstanceId, OwnerToken),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (RuntimeClientException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await ReacquireAsync(client, lease, lease.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                ReconcileHeartbeat(renewed, lease.RuntimeInstanceId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RuntimeClientException exception) when (exception.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
            }
            catch (Exception exception)
            {
                ReportFault("heartbeat", exception);
            }
        }
    }

    private async Task ReacquireAsync(
        IDevPackageOwnerClient client,
        DevPackageOwnerLeaseResponse expectedLease,
        Guid runtimeInstanceId,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DevPackageOwnerMutationRequest request;
            lock (_syncRoot)
            {
                if (_releasing || !LeaseMatches(_lease, expectedLease))
                {
                    return;
                }

                var desiredFolders = _pendingMutation?.Folders
                    ?? _desiredFolders
                    ?? throw new InvalidOperationException("The development package owner lease has no desired folder set.");
                RotateOwnerTokenIfItMatchesTransport();
                request = _pendingMutation is { } pending
                          && pending.RuntimeInstanceId == runtimeInstanceId
                          && pending.Revision == 1
                          && string.Equals(pending.OwnerToken, OwnerToken, StringComparison.Ordinal)
                          && FoldersEqual(pending.Folders, desiredFolders)
                    ? pending
                    : CreateMutation(runtimeInstanceId, 1, desiredFolders);
                _pendingMutation = request;
            }

            var replacement = await client.ReplaceDevPackageOwnerAsync(
                OwnerId,
                request,
                cancellationToken).ConfigureAwait(false);
            ValidateResponse(replacement, request);
            lock (_syncRoot)
            {
                if (LeaseMatches(_lease, expectedLease))
                {
                    _lease = replacement;
                    _pendingMutation = null;
                    _desiredFolders = request.Folders;
                }
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private void ReconcileHeartbeat(DevPackageOwnerLeaseResponse response, Guid runtimeInstanceId)
    {
        ValidateResponseIdentity(response, runtimeInstanceId);
        lock (_syncRoot)
        {
            if (LeaseMutationMatches(_lease, response))
            {
                _lease = response;
                return;
            }
            if (_pendingMutation is { } pending && MutationMatches(pending, response))
            {
                _lease = response;
                _pendingMutation = null;
                _desiredFolders = pending.Folders;
                return;
            }
        }

        throw new InvalidDataException("Runtime returned an unexpected development package owner heartbeat revision.");
    }

    private void CommitMutation(
        DevPackageOwnerMutationRequest request,
        DevPackageOwnerLeaseResponse response)
    {
        ValidateResponse(response, request);
        lock (_syncRoot)
        {
            if (!MutationMatches(_pendingMutation, request)
                && !LeaseMutationMatches(_lease, response))
            {
                throw new InvalidDataException("Runtime returned a stale development package owner mutation response.");
            }

            _lease = response;
            if (MutationMatches(_pendingMutation, request))
            {
                _pendingMutation = null;
            }
            _desiredFolders = request.Folders;
        }
    }

    private DevPackageOwnerMutationRequest CreateMutation(
        Guid runtimeInstanceId,
        long revision,
        IReadOnlyList<DevPackageOwnerFolder> folders)
    {
        var connection = _getConnectionInfo();
        var includeProcessFence = connection?.RuntimeUrl.IsLoopback == true;
        return new DevPackageOwnerMutationRequest(
            runtimeInstanceId,
            OwnerToken,
            Guid.NewGuid().ToString("N"),
            revision,
            folders,
            includeProcessFence ? _processId : null,
            includeProcessFence ? _processStartedAtUtc : null);
    }

    private void ResetStaleRuntimeState(Guid runtimeInstanceId)
    {
        lock (_syncRoot)
        {
            if (_lease is not null && _lease.RuntimeInstanceId != runtimeInstanceId)
            {
                _lease = null;
                _desiredFolders = null;
            }
            if (_pendingMutation is not null && _pendingMutation.RuntimeInstanceId != runtimeInstanceId)
            {
                _pendingMutation = null;
            }
        }
    }

    private void ClearOwnershipState(Guid? runtimeInstanceId = null)
    {
        lock (_syncRoot)
        {
            if (runtimeInstanceId is null || _lease?.RuntimeInstanceId == runtimeInstanceId)
            {
                _lease = null;
            }
            if (runtimeInstanceId is null || _pendingMutation?.RuntimeInstanceId == runtimeInstanceId)
            {
                _pendingMutation = null;
            }
            if (_lease is null && _pendingMutation is null)
            {
                _desiredFolders = null;
            }
        }
    }

    private DevPackageOwnerLeaseResponse? GetLease(Guid runtimeInstanceId)
    {
        lock (_syncRoot)
        {
            return _lease?.RuntimeInstanceId == runtimeInstanceId ? _lease : null;
        }
    }

    private IReadOnlyList<DevPackageOwnerFolder>? GetDesiredFolders()
    {
        lock (_syncRoot)
        {
            return _desiredFolders;
        }
    }

    private void SetPendingMutation(DevPackageOwnerMutationRequest request)
    {
        lock (_syncRoot)
        {
            _pendingMutation = request;
        }
    }

    private void EnsureHeartbeatStarted()
    {
        lock (_syncRoot)
        {
            if (_releasing || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            if (_heartbeatCancellation is not null && !_heartbeatTask.IsCompleted)
            {
                return;
            }

            _heartbeatCancellation?.Dispose();
            _heartbeatCancellation = new CancellationTokenSource();
            _heartbeatTask = RunHeartbeatAsync(_heartbeatCancellation.Token);
        }
    }

    private string CreateSeparatedOwnerToken()
    {
        string token;
        do
        {
            token = RuntimeBearerToken.Create();
        }
        while (string.Equals(token, _getConnectionInfo()?.BearerToken, StringComparison.Ordinal));
        return token;
    }

    private void EnsureOwnerTokenSeparatedWhenUnowned()
    {
        lock (_syncRoot)
        {
            if (_lease is null && _pendingMutation is null)
            {
                RotateOwnerTokenIfItMatchesTransport();
            }
        }
    }

    private void RotateOwnerTokenIfItMatchesTransport()
    {
        if (string.Equals(OwnerToken, _getConnectionInfo()?.BearerToken, StringComparison.Ordinal))
        {
            OwnerToken = CreateSeparatedOwnerToken();
        }
    }

    private void ValidateResponse(
        DevPackageOwnerLeaseResponse response,
        DevPackageOwnerMutationRequest request)
    {
        ValidateResponseIdentity(response, request.RuntimeInstanceId);
        if (!string.Equals(response.MutationId, request.MutationId, StringComparison.Ordinal)
            || response.Revision != request.Revision)
        {
            throw new InvalidDataException("Runtime returned an invalid development package owner mutation response.");
        }
    }

    private void ValidateResponseIdentity(DevPackageOwnerLeaseResponse response, Guid runtimeInstanceId)
    {
        if (response.RuntimeInstanceId != runtimeInstanceId
            || !string.Equals(response.OwnerId, OwnerId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Runtime returned an invalid development package owner lease response.");
        }
    }

    private void ReportFault(string operation, Exception exception)
    {
        try
        {
            _reportFault?.Invoke(operation, exception);
        }
        catch
        {
            // A diagnostic observer cannot be allowed to terminate lease maintenance.
        }
    }

    private static bool FoldersEqual(
        IReadOnlyList<DevPackageOwnerFolder>? left,
        IReadOnlyList<DevPackageOwnerFolder> right)
        => left is not null
           && left.Count == right.Count
           && left.Zip(right).All(pair => pair.First.Watch == pair.Second.Watch
                                          && PathComparer.Equals(pair.First.Folder, pair.Second.Folder));

    private static bool LeaseMatches(
        DevPackageOwnerLeaseResponse? left,
        DevPackageOwnerLeaseResponse right)
        => left is not null
           && left.RuntimeInstanceId == right.RuntimeInstanceId
           && left.Revision == right.Revision
           && string.Equals(left.MutationId, right.MutationId, StringComparison.Ordinal);

    private static bool LeaseMutationMatches(
        DevPackageOwnerLeaseResponse? left,
        DevPackageOwnerLeaseResponse right)
        => LeaseMatches(left, right);

    private static bool MutationMatches(
        DevPackageOwnerMutationRequest? request,
        DevPackageOwnerLeaseResponse response)
        => request is not null
           && request.RuntimeInstanceId == response.RuntimeInstanceId
           && request.Revision == response.Revision
           && string.Equals(request.MutationId, response.MutationId, StringComparison.Ordinal);

    private static bool MutationMatches(
        DevPackageOwnerMutationRequest? left,
        DevPackageOwnerMutationRequest right)
        => left is not null
           && left.RuntimeInstanceId == right.RuntimeInstanceId
           && left.Revision == right.Revision
           && string.Equals(left.MutationId, right.MutationId, StringComparison.Ordinal);

    private static void ValidateOwnerIdPrefix(string ownerIdPrefix)
    {
        if (string.IsNullOrWhiteSpace(ownerIdPrefix)
            || ownerIdPrefix.Length > 95
            || ownerIdPrefix.Any(character => !char.IsAsciiLetterOrDigit(character)
                                               && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("A valid development package owner id prefix is required.", nameof(ownerIdPrefix));
        }
    }
}

public sealed partial class RuntimeManagementClient : IDevPackageOwnerClient
{
    public DevPackageOwnerSession CreateDevPackageOwnerSession(
        string ownerIdPrefix,
        Action<string, Exception>? reportFault = null,
        DevPackageOwnerSessionOptions? options = null)
        => new(_transport, ownerIdPrefix, reportFault, options);
}
