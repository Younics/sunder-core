using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Worker.Internal;

internal sealed class WorkerRpcCallScope : ISunderRpcCallScope, IWorkerContentHandleOwner
{
    private readonly WorkerRpcClient _client;
    private readonly WorkerEnvironment _environment;
    private readonly WorkerLimits _limits;
    private readonly CancellationTokenSource _revocation = new();
    private Timer? _deadlineTimer;
    private readonly object _streamGate = new();
    private readonly object _cleanupGate = new();
    private readonly HashSet<WorkerContentReadStream> _streams = [];
    private Task _abandonedContentReleases = Task.CompletedTask;
    private Task? _cleanupTask;
    private int _disposed;
    private int _expired;

    public WorkerRpcCallScope(
        WorkerRpcClient client,
        WorkerEnvironment environment,
        WorkerLimits limits,
        string scopeId,
        DateTimeOffset deadlineUtc)
    {
        _client = client;
        _environment = environment;
        _limits = limits;
        ScopeId = scopeId;
        DeadlineUtc = deadlineUtc;
    }

    internal void ArmDeadline()
    {
        var dueTime = DeadlineUtc - DateTimeOffset.UtcNow;
        if (dueTime <= TimeSpan.Zero)
        {
            Expire();
        }
        else
        {
            _deadlineTimer = new Timer(
                static state => ((WorkerRpcCallScope)state!).Expire(),
                this,
                dueTime,
                Timeout.InfiniteTimeSpan);
        }
    }

    internal string ScopeId { get; }

    public DateTimeOffset DeadlineUtc { get; }

    public async ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken = default)
        => await ExecuteScopeCallAsync(
            token => _client.GetProviderAsync(ScopeId, endpoint, token),
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> TryReportInvariantViolationAsync(
        SunderRpcEndpointReference endpoint,
        Exception exception,
        CancellationToken cancellationToken = default)
        => await ExecuteScopeCallAsync(
            token => _client.TryReportInvariantViolationAsync(ScopeId, endpoint, exception, token),
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
        string contractId,
        CancellationToken cancellationToken = default)
        => await ExecuteScopeCallAsync(
            token => _client.DiscoverAsync(ScopeId, contractId, token),
            cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
        long afterRevision,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CreateLinkedCancellation(cancellationToken);
        await using var enumerator = _client.WatchAsync(
                ScopeId,
                afterRevision,
                afterSequence,
                linked.Token)
            .GetAsyncEnumerator(linked.Token);
        while (true)
        {
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (DeadlineExpired())
            {
                throw DeadlineExceeded();
            }
            if (!moved) yield break;
            yield return enumerator.Current;
        }
    }

    public async ValueTask<JsonElement> InvokeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        => await ExecuteScopeCallAsync(
            token => _client.InvokeAsync(
                ScopeId,
                endpoint,
                serviceId,
                methodId,
                request,
                options,
                token),
            cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<JsonElement> SubscribeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CreateLinkedCancellation(cancellationToken);
        await using var enumerator = _client.SubscribeAsync(
                ScopeId,
                endpoint,
                serviceId,
                methodId,
                request,
                options,
                linked.Token)
            .GetAsyncEnumerator(linked.Token);
        while (true)
        {
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (DeadlineExpired())
            {
                throw DeadlineExceeded();
            }
            if (!moved) yield break;
            yield return enumerator.Current;
        }
    }

    public async ValueTask<SunderRpcContentReference> RegisterContentAsync(
        SunderRpcEndpointReference endpoint,
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("RPC content source must be readable.", nameof(source));
        WorkerInvocationAuthority.ValidateOptions(_limits, options);
        EnsureActive();
        var dataPath = _environment.PackageDataPath
                       ?? throw WorkerInvocationAuthority.ContentPathsUnavailable();
        var directory = Path.Combine(dataPath, "tmp", "sunder-sdk-worker", "rpc-content");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "caller-" + Guid.NewGuid().ToString("N") + ".content");
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long length = 0;
        try
        {
            using var linked = CreateLinkedCancellation(cancellationToken);
            await using (var destination = new FileStream(
                             filePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.Read,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    length += read;
                    if (length > _limits.MaximumContentBytes)
                    {
                        throw new InvalidDataException(
                            $"RPC content exceeds the {_limits.MaximumContentBytes} byte worker limit.");
                    }
                    await destination.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                }
                await destination.FlushAsync(linked.Token).ConfigureAwait(false);
            }

            if (options.Length is { } expectedLength && expectedLength != length)
            {
                throw new InvalidDataException("RPC content length does not match the declared length.");
            }
            return await RegisterContentFileCoreAsync(
                endpoint,
                filePath,
                options,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (DeadlineExpired())
        {
            throw DeadlineExceeded();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            WorkerInvocationAuthority.TryDelete(filePath);
        }
    }

    public async ValueTask<SunderRpcContentReference> RegisterContentFileAsync(
        SunderRpcEndpointReference endpoint,
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        WorkerInvocationAuthority.ValidateOptions(_limits, options);
        EnsureActive();
        return await ExecuteScopeCallAsync(
            token => RegisterContentFileCoreAsync(
                endpoint,
                WorkerInvocationAuthority.ResolveWorkerOwnedFile(_environment, filePath),
                options,
                token),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Stream> OpenContentAsync(
        SunderRpcContentReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        WorkerInvocationAuthority.ValidateReference(_limits, reference);
        EnsureActive();
        var reservation = _client.ReserveContentHandle();
        var retained = false;
        WorkerContentFileHandle? handle = null;
        FileStream? file = null;
        WorkerContentReadStream? openedStream = null;
        try
        {
            using var linked = CreateLinkedCancellation(cancellationToken);
            await _client.OpenScopeContentAsync(
                ScopeId,
                reference,
                IsContentAuthorityActive,
                ReleaseAbandonedContentAsync,
                value =>
                {
                    handle = _client.ParseHostValue(value, WorkerWire.ReadContentFileHandle);
                    var filePath = WorkerInvocationAuthority.ResolveHostContentFile(
                        _environment,
                        handle.FilePath);
                    file = new FileStream(
                        filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete,
                        128 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (file.Length != reference.Length)
                    {
                        throw new WorkerProtocolException("Host RPC content file length does not match its reference.");
                    }

                    var stream = new WorkerContentReadStream(
                        file,
                        this,
                        handle.HandleId,
                        reservation,
                        _revocation.Token);
                    lock (_streamGate)
                    {
                        EnsureActive();
                        _streams.Add(stream);
                    }
                    openedStream = stream;
                    retained = true;
                    file = null;
                    return ValueTask.CompletedTask;
                },
                reservation,
                linked.Token).ConfigureAwait(false);
            return openedStream!;
        }
        catch (Exception exception)
        {
            if (file is not null)
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }
            if (exception is WorkerProtocolException protocolException)
            {
                throw _client.FailProtocol(protocolException);
            }
            if (!IsExpired && DateTimeOffset.UtcNow >= DeadlineUtc) Expire();
            if (!IsContentAuthorityActive())
            {
                await BeginCleanup().ConfigureAwait(false);
                throw IsExpired
                    ? DeadlineExceeded()
                    : new OperationCanceledException(_revocation.Token);
            }
            if (exception is OperationCanceledException)
            {
                if (handle is not null)
                {
                    retained = true;
                    _client.ReleaseScopeContentDetached(
                        ScopeId,
                        handle.HandleId,
                        reservation);
                }
                throw;
            }
            if (exception is SunderRpcException) throw;
            throw _client.FailProtocol(new WorkerProtocolException(
                "Host RPC content file could not be opened safely.",
                exception));
        }
        finally
        {
            if (!retained) reservation.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await BeginCleanup().ConfigureAwait(false);
            return;
        }
        _deadlineTimer?.Dispose();
        await BeginCleanup().ConfigureAwait(false);
    }

    public async ValueTask ReleaseAsync(WorkerContentReadStream stream, string handleId)
    {
        ValueTask release = ValueTask.CompletedTask;
        var removed = false;
        lock (_streamGate)
        {
            removed = _streams.Remove(stream);
            if (removed && Volatile.Read(ref _disposed) == 0 && !IsExpired)
            {
                release = _client.ReleaseScopeContentAsync(ScopeId, handleId, _revocation.Token);
            }
        }
        try
        {
            await release.ConfigureAwait(false);
        }
        finally
        {
            if (removed) stream.ReleaseReservation();
        }
    }

    internal Task RevokeFromRuntime()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _deadlineTimer?.Dispose();
        return BeginCleanup();
    }

    internal ValueTask ReleaseAbandonedContentAsync(string handleId)
    {
        Task? cleanup;
        lock (_cleanupGate)
        {
            cleanup = _cleanupTask;
            if (cleanup is null && IsContentAuthorityActive())
            {
                Task release;
                try
                {
                    release = ReleaseAbandonedContentCoreAsync(handleId);
                }
                catch (Exception exception)
                {
                    release = Task.FromException(exception);
                }
                _abandonedContentReleases = Task.WhenAll(_abandonedContentReleases, release);
                return new ValueTask(release);
            }
        }
        return new ValueTask(cleanup ?? BeginCleanup());
    }

    private async Task ReleaseAbandonedContentCoreAsync(string handleId)
    {
        try
        {
            await _client.ReleaseScopeContentAsync(
                ScopeId,
                handleId,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (
            exception.Error.Kind == SunderRpcErrorKind.Unavailable
            && string.Equals(exception.Error.Code, "rpc.scope.unavailable", StringComparison.Ordinal))
        {
            // Host deadline revocation already released the scope-owned handle.
        }
    }

    private async ValueTask<SunderRpcContentReference> RegisterContentFileCoreAsync(
        SunderRpcEndpointReference endpoint,
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken)
    {
        var information = new FileInfo(filePath);
        if (!information.Exists) throw new FileNotFoundException("RPC content file does not exist.", filePath);
        if (information.Length > _limits.MaximumContentBytes)
        {
            throw new InvalidDataException(
                $"RPC content exceeds the {_limits.MaximumContentBytes} byte worker limit.");
        }
        if (options.Length is { } expectedLength && expectedLength != information.Length)
        {
            throw new InvalidDataException("RPC content file length does not match the declared length.");
        }

        using var linked = CreateLinkedCancellation(cancellationToken);
        var value = await _client.RegisterScopeContentFileAsync(
            ScopeId,
            endpoint,
            filePath,
            options,
            linked.Token).ConfigureAwait(false);
        return _client.ParseHostValue(value, WorkerWire.ReadContentReference);
    }

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken cancellationToken)
    {
        EnsureActive();
        return CancellationTokenSource.CreateLinkedTokenSource(_revocation.Token, cancellationToken);
    }

    private async ValueTask<T> ExecuteScopeCallAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        using var linked = CreateLinkedCancellation(cancellationToken);
        try
        {
            return await operation(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (DeadlineExpired())
        {
            throw DeadlineExceeded();
        }
    }

    private bool DeadlineExpired()
    {
        if (!IsExpired && DateTimeOffset.UtcNow >= DeadlineUtc) Expire();
        return IsExpired;
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsExpired || DateTimeOffset.UtcNow >= DeadlineUtc)
        {
            Expire();
            throw DeadlineExceeded();
        }
    }

    private WorkerContentReadStream[] RevokeLocal()
    {
        try
        {
            _revocation.Cancel();
        }
        catch (AggregateException)
        {
        }

        WorkerContentReadStream[] streams;
        lock (_streamGate)
        {
            streams = _streams.ToArray();
            _streams.Clear();
        }
        foreach (var stream in streams)
        {
            stream.Revoke();
        }
        return streams;
    }

    private bool IsExpired => Volatile.Read(ref _expired) != 0;

    private bool IsContentAuthorityActive()
        => Volatile.Read(ref _disposed) == 0 && !IsExpired && !_revocation.IsCancellationRequested;

    private void Expire()
    {
        if (Interlocked.Exchange(ref _expired, 1) != 0) return;
        _ = ObserveExpiredCleanupAsync(BeginCleanup());
    }

    private Task BeginCleanup()
    {
        TaskCompletionSource completion;
        Task abandonedContentReleases;
        lock (_cleanupGate)
        {
            if (_cleanupTask is not null) return _cleanupTask;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _cleanupTask = completion.Task;
            abandonedContentReleases = _abandonedContentReleases;
        }
        _ = CompleteCleanupAsync(completion, abandonedContentReleases);
        return completion.Task;
    }

    private async Task CompleteCleanupAsync(
        TaskCompletionSource completion,
        Task abandonedContentReleases)
    {
        try
        {
            try
            {
                await abandonedContentReleases.ConfigureAwait(false);
            }
            finally
            {
                await _client.CloseScopeAsync(this, RevokeLocal()).ConfigureAwait(false);
            }
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task ObserveExpiredCleanupAsync(Task cleanup)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_revocation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _client.FailScopeCleanup(exception);
        }
    }

    private static SunderRpcException DeadlineExceeded()
        => SunderRpcException.Infrastructure(
            SunderRpcErrorKind.DeadlineExceeded,
            "rpc.call.deadline-exceeded",
            "The RPC call deadline elapsed.");
}
