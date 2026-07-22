using System.Diagnostics;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.App.Services;

public sealed class DevPackageOwnerSession : IAsyncDisposable
{
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(5);
    private readonly IRuntimeApiClientFactory _clients;
    private readonly RuntimeConnectionState _connectionState;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly object _syncRoot = new();
    private readonly int _processId = Environment.ProcessId;
    private readonly DateTimeOffset _processStartedAtUtc;
    private DevPackageOwnerLeaseResponse? _lease;
    private DevPackageOwnerMutationRequest? _pendingMutation;
    private IReadOnlyList<DevPackageOwnerFolder>? _desiredFolders;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task _heartbeatTask = Task.CompletedTask;
    private int _disposed;

    public DevPackageOwnerSession(
        IRuntimeApiClientFactory clients,
        RuntimeConnectionState connectionState)
    {
        _clients = clients;
        _connectionState = connectionState;
        OwnerId = $"app-{Guid.NewGuid():N}";
        do
        {
            OwnerToken = RuntimeBearerToken.Create();
        }
        while (string.Equals(
            OwnerToken,
            connectionState.ConnectionInfo?.BearerToken,
            StringComparison.Ordinal));

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
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = _clients.CreateClient<IRuntimeDevPackageOwnerClient>();
            var handshake = await client.GetRuntimeHandshakeAsync(cancellationToken).ConfigureAwait(false);
            DevPackageOwnerLeaseResponse? current;
            lock (_syncRoot)
            {
                current = _lease;
                if (current is null
                    && _pendingMutation is null
                    && string.Equals(OwnerToken, _connectionState.ConnectionInfo?.BearerToken, StringComparison.Ordinal))
                {
                    OwnerToken = RuntimeBearerToken.Create();
                }
            }

            if (current is not null && current.RuntimeInstanceId != handshake.RuntimeInstanceId)
            {
                current = null;
                lock (_syncRoot)
                {
                    _heartbeatCancellation?.Cancel();
                    _heartbeatCancellation?.Dispose();
                    _heartbeatCancellation = null;
                    _heartbeatTask = Task.CompletedTask;
                    _lease = null;
                    _pendingMutation = null;
                    _desiredFolders = null;
                }
            }

            var desiredFolders = folders
                .Select(folder => new DevPackageOwnerFolder(Path.GetFullPath(folder), watch))
                .ToArray();
            DevPackageOwnerMutationRequest request;
            lock (_syncRoot)
            {
                request = _pendingMutation is { } pending
                          && pending.RuntimeInstanceId == handshake.RuntimeInstanceId
                          && FoldersEqual(pending.Folders, desiredFolders)
                    ? pending
                    : new DevPackageOwnerMutationRequest(
                        handshake.RuntimeInstanceId,
                        OwnerToken,
                        Guid.NewGuid().ToString("N"),
                        current is null ? 1 : checked(current.Revision + 1),
                        desiredFolders,
                        _connectionState.RuntimeUrl.IsLoopback ? _processId : null,
                        _connectionState.RuntimeUrl.IsLoopback ? _processStartedAtUtc : null);
                _pendingMutation = request;
            }
            var lease = await client.ReplaceDevPackageOwnerAsync(OwnerId, request, cancellationToken).ConfigureAwait(false);
            ValidateResponse(lease, handshake.RuntimeInstanceId, request.MutationId, request.Revision);
            lock (_syncRoot)
            {
                _lease = lease;
                _pendingMutation = null;
                _desiredFolders = request.Folders;
                if (_heartbeatCancellation is null)
                {
                    _heartbeatCancellation = new CancellationTokenSource();
                    _heartbeatTask = RunHeartbeatAsync(_heartbeatCancellation.Token);
                }
            }

            return lease;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DevPackageOwnerLeaseResponse? lease;
            CancellationTokenSource? heartbeatCancellation;
            Task heartbeatTask;
            lock (_syncRoot)
            {
                lease = _lease;
                _pendingMutation = null;
                _desiredFolders = null;
                heartbeatCancellation = _heartbeatCancellation;
                _heartbeatCancellation = null;
                heartbeatTask = _heartbeatTask;
                _heartbeatTask = Task.CompletedTask;
            }

            if (lease is null)
            {
                heartbeatCancellation?.Dispose();
                return;
            }

            heartbeatCancellation?.Cancel();
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                heartbeatCancellation?.Dispose();
            }

            using var client = _clients.CreateClient<IRuntimeDevPackageOwnerClient>();
            await client.ReleaseDevPackageOwnerAsync(
                OwnerId,
                new DevPackageOwnerReleaseRequest(lease.RuntimeInstanceId, OwnerToken),
                cancellationToken).ConfigureAwait(false);
            lock (_syncRoot)
            {
                if (_lease?.RuntimeInstanceId == lease.RuntimeInstanceId
                    && _lease.Revision == lease.Revision)
                {
                    _lease = null;
                }
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        using var cleanup = new CancellationTokenSource(CleanupBudget);
        try
        {
            await ReleaseAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError("Failed to release the App dev package owner lease during shutdown.", exception);
        }
        finally
        {
            _mutationGate.Dispose();
        }
    }

    private async Task RunHeartbeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DevPackageOwnerLeaseResponse? lease;
            lock (_syncRoot)
            {
                lease = _lease;
            }
            if (lease is null)
            {
                return;
            }

            var remaining = lease.ExpiresAtUtc - DateTimeOffset.UtcNow;
            var delay = TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds / 3, 1000, 10000));
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                using var client = _clients.CreateClient<IRuntimeDevPackageOwnerClient>();
                var handshake = await client.GetRuntimeHandshakeAsync(cancellationToken).ConfigureAwait(false);
                if (handshake.RuntimeInstanceId != lease.RuntimeInstanceId)
                {
                    await ReacquireAfterRuntimeReplacementAsync(
                        client,
                        handshake.RuntimeInstanceId,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var renewed = await client.HeartbeatDevPackageOwnerAsync(
                    OwnerId,
                    new DevPackageOwnerHeartbeatRequest(lease.RuntimeInstanceId, OwnerToken),
                    cancellationToken).ConfigureAwait(false);
                ValidateResponse(renewed, lease.RuntimeInstanceId, lease.MutationId, lease.Revision);
                lock (_syncRoot)
                {
                    if (_lease?.RuntimeInstanceId == renewed.RuntimeInstanceId
                        && _lease.Revision == renewed.Revision)
                    {
                        _lease = renewed;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                AppSessionLog.WriteError("The App dev package owner heartbeat failed; the Runtime TTL will fence a lost App session.", exception);
            }
        }
    }

    private async Task ReacquireAfterRuntimeReplacementAsync(
        IRuntimeDevPackageOwnerClient client,
        Guid runtimeInstanceId,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DevPackageOwnerLeaseResponse? current;
            DevPackageOwnerMutationRequest request;
            lock (_syncRoot)
            {
                current = _lease;
                if (current is null || current.RuntimeInstanceId == runtimeInstanceId)
                {
                    return;
                }
                var desiredFolders = _desiredFolders
                    ?? throw new InvalidOperationException("The App dev package owner lease has no desired folder set.");
                request = _pendingMutation is { } pending
                          && pending.RuntimeInstanceId == runtimeInstanceId
                          && FoldersEqual(pending.Folders, desiredFolders)
                    ? pending
                    : new DevPackageOwnerMutationRequest(
                        runtimeInstanceId,
                        OwnerToken,
                        Guid.NewGuid().ToString("N"),
                        1,
                        desiredFolders,
                        _connectionState.RuntimeUrl.IsLoopback ? _processId : null,
                        _connectionState.RuntimeUrl.IsLoopback ? _processStartedAtUtc : null);
                _pendingMutation = request;
            }

            var replacement = await client.ReplaceDevPackageOwnerAsync(
                OwnerId,
                request,
                cancellationToken).ConfigureAwait(false);
            ValidateResponse(replacement, runtimeInstanceId, request.MutationId, request.Revision);
            lock (_syncRoot)
            {
                if (_lease?.RuntimeInstanceId == current.RuntimeInstanceId
                    && _lease.Revision == current.Revision)
                {
                    _lease = replacement;
                    _pendingMutation = null;
                }
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private void ValidateResponse(
        DevPackageOwnerLeaseResponse response,
        Guid runtimeInstanceId,
        string mutationId,
        long revision)
    {
        if (response.RuntimeInstanceId != runtimeInstanceId
            || !string.Equals(response.OwnerId, OwnerId, StringComparison.Ordinal)
            || !string.Equals(response.MutationId, mutationId, StringComparison.Ordinal)
            || response.Revision != revision)
        {
            throw new InvalidDataException("Runtime returned an invalid dev package owner lease response.");
        }
    }

    private static bool FoldersEqual(
        IReadOnlyList<DevPackageOwnerFolder> left,
        IReadOnlyList<DevPackageOwnerFolder> right)
        => left.Count == right.Count
           && left.Zip(right).All(pair => pair.First == pair.Second);
}
