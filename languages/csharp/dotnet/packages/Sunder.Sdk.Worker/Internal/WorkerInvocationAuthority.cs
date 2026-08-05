using System.Buffers;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Worker.Internal;

internal interface IWorkerContentHandleOwner
{
    ValueTask ReleaseAsync(WorkerContentReadStream stream, string handleId);
}

internal sealed class WorkerInvocationAuthority : ISunderRpcInvocationAuthority, IWorkerContentHandleOwner
{
    private readonly SunderWorkerRuntime _runtime;
    private readonly WorkerRpcClient _client;
    private readonly WorkerEnvironment _environment;
    private readonly WorkerLimits _limits;
    private readonly string _invocationId;
    private readonly CancellationTokenSource _revocation;
    private readonly object _streamGate = new();
    private readonly HashSet<WorkerContentReadStream> _streams = [];
    private int _revoked;

    public WorkerInvocationAuthority(
        SunderWorkerRuntime runtime,
        WorkerRpcClient client,
        WorkerEnvironment environment,
        WorkerLimits limits,
        string invocationId,
        CancellationToken invocationCancellation)
    {
        _runtime = runtime;
        _client = client;
        _environment = environment;
        _limits = limits;
        _invocationId = invocationId;
        _revocation = CancellationTokenSource.CreateLinkedTokenSource(invocationCancellation);
    }

    public CancellationToken RevocationToken => _revocation.Token;

    public async ValueTask<SunderRpcContentReference> RegisterContentAsync(
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("RPC content source must be readable.", nameof(source));
        ValidateOptions(_limits, options);
        EnsureActive();
        var dataPath = _environment.PackageDataPath ?? throw ContentPathsUnavailable();
        var directory = Path.Combine(dataPath, "tmp", "sunder-sdk-worker", "rpc-content");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "provider-" + Guid.NewGuid().ToString("N") + ".content");
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long length = 0;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _revocation.Token,
                cancellationToken);
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
            return await RegisterContentFileCoreAsync(filePath, options, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            TryDelete(filePath);
        }
    }

    public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ValidateOptions(_limits, options);
        EnsureActive();
        return RegisterContentFileCoreAsync(
            ResolveWorkerOwnedFile(_environment, filePath),
            options,
            cancellationToken);
    }

    public async ValueTask<Stream> OpenContentAsync(
        SunderRpcContentReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ValidateReference(_limits, reference);
        EnsureActive();
        var reservation = _client.ReserveContentHandle();
        var retained = false;
        WorkerContentFileHandle? handle = null;
        FileStream? file = null;
        WorkerContentReadStream? openedStream = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _revocation.Token,
                cancellationToken);
            await _client.OpenContentAsync(
                _invocationId,
                reference,
                IsAuthorityActive,
                value =>
                {
                    handle = ParseHostValue(value, WorkerWire.ReadContentFileHandle);
                    var filePath = ResolveHostContentFile(_environment, handle.FilePath);
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
            if (!IsAuthorityActive())
            {
                throw new OperationCanceledException(_revocation.Token);
            }
            if (exception is OperationCanceledException)
            {
                if (handle is not null)
                {
                    retained = true;
                    _client.ReleaseInvocationContentDetached(
                        _invocationId,
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

    public void Revoke()
    {
        if (Interlocked.Exchange(ref _revoked, 1) != 0) return;
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
            stream.ReleaseReservation();
        }
    }

    public async ValueTask ReleaseAsync(WorkerContentReadStream stream, string handleId)
    {
        var removed = false;
        lock (_streamGate)
        {
            removed = _streams.Remove(stream);
        }
        try
        {
            if (removed) await ReleaseOpenedAsync(handleId).ConfigureAwait(false);
        }
        finally
        {
            if (removed) stream.ReleaseReservation();
        }
    }

    private async ValueTask<SunderRpcContentReference> RegisterContentFileCoreAsync(
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

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _revocation.Token,
            cancellationToken);
        var value = await _client.RegisterContentFileAsync(
            _invocationId,
            filePath,
            options,
            linked.Token).ConfigureAwait(false);
        return ParseHostValue(value, WorkerWire.ReadContentReference);
    }

    private async ValueTask ReleaseOpenedAsync(string handleId)
    {
        if (Volatile.Read(ref _revoked) != 0 || _revocation.IsCancellationRequested) return;
        try
        {
            await _client.ReleaseInvocationContentAsync(
                _invocationId,
                handleId,
                _revocation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_revocation.IsCancellationRequested)
        {
        }
    }

    private T ParseHostValue<T>(System.Text.Json.JsonElement value, Func<System.Text.Json.JsonElement, T> parser)
    {
        try
        {
            return parser(value);
        }
        catch (WorkerProtocolException exception)
        {
            _runtime.Fail(exception);
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            var protocol = new WorkerProtocolException("Host RPC content result is malformed.", exception);
            _runtime.Fail(protocol);
            throw protocol;
        }
    }

    internal static string ResolveWorkerOwnedFile(WorkerEnvironment environment, string filePath)
    {
        string fullPath;
        try
        {
            var basePath = environment.PackageContentPath ?? environment.PackageDataPath
                ?? throw ContentPathsUnavailable();
            fullPath = Path.GetFullPath(filePath, basePath);
        }
        catch (SunderRpcException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("RPC content file path is invalid.", nameof(filePath), exception);
        }

        if (!IsWithin(fullPath, environment.PackageContentPath)
            && !IsWithin(fullPath, environment.PackageDataPath))
        {
            throw new ArgumentException(
                "RPC content files must be inside the package content or private data directory.",
                nameof(filePath));
        }
        return fullPath;
    }

    internal static string ResolveHostContentFile(WorkerEnvironment environment, string filePath)
    {
        var dataPath = environment.PackageDataPath ?? throw ContentPathsUnavailable();
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new WorkerProtocolException("Host RPC content file path is invalid.", exception);
        }
        if (!IsWithin(fullPath, dataPath))
        {
            throw new WorkerProtocolException("Host RPC content file escaped the package private data directory.");
        }
        return fullPath;
    }

    internal static void ValidateOptions(
        WorkerLimits limits,
        SunderRpcContentRegistrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.MediaType)
            || options.MediaType.Length > 128
            || string.IsNullOrWhiteSpace(options.FileName)
            || options.FileName.Length > 255
            || options.Length is < 0
            || options.Length > limits.MaximumContentBytes
            || options.MaximumUses is < 1
            || options.MaximumUses > limits.MaximumContentUses
            || options.Repeatability == SunderRpcContentRepeatability.SingleUse && options.MaximumUses != 1
            || options.Repeatability is not (SunderRpcContentRepeatability.SingleUse or SunderRpcContentRepeatability.Repeatable))
        {
            throw new ArgumentException("The RPC content registration options are invalid.", nameof(options));
        }
    }

    internal static void ValidateReference(WorkerLimits limits, SunderRpcContentReference reference)
    {
        if (!WorkerJson.IsSafeId(reference.Id)
            || reference.Id.Length > 128
            || reference.Length < 0
            || reference.Length > limits.MaximumContentBytes
            || reference.Sha256.Length != 64
            || reference.Sha256.Any(static character => !char.IsAsciiHexDigit(character))
            || string.IsNullOrEmpty(reference.MediaType)
            || reference.MediaType.Length > 128
            || string.IsNullOrEmpty(reference.FileName)
            || reference.FileName.Length > 255
            || reference.Repeatability is not (SunderRpcContentRepeatability.SingleUse or SunderRpcContentRepeatability.Repeatable))
        {
            throw new ArgumentException("The RPC content reference is invalid.", nameof(reference));
        }
    }

    private void EnsureActive()
    {
        if (Volatile.Read(ref _revoked) != 0 || _revocation.IsCancellationRequested)
        {
            throw SunderRpcException.Infrastructure(
                SunderRpcErrorKind.Unavailable,
                "rpc.content.authority-revoked",
                "The RPC invocation content authority has ended.");
        }
    }

    private bool IsAuthorityActive()
        => Volatile.Read(ref _revoked) == 0 && !_revocation.IsCancellationRequested;

    private static bool IsWithin(string path, string? directory)
    {
        if (directory is null) return false;
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative)
               && !string.Equals(relative, "..", StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static SunderRpcException ContentPathsUnavailable()
        => SunderRpcException.Infrastructure(
            SunderRpcErrorKind.Unavailable,
            "rpc.content.worker-paths-unavailable",
            "The Host did not provide worker content paths for this activation.");

    internal static void TryDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
        }
    }
}

internal sealed class WorkerContentReadStream : Stream
{
    private readonly Stream _inner;
    private readonly IWorkerContentHandleOwner _owner;
    private readonly string _handleId;
    private readonly WorkerResourceReservation _reservation;
    private readonly CancellationToken _revocationToken;
    private int _disposed;

    public WorkerContentReadStream(
        Stream inner,
        IWorkerContentHandleOwner owner,
        string handleId,
        WorkerResourceReservation reservation,
        CancellationToken revocationToken)
    {
        _inner = inner;
        _owner = owner;
        _handleId = handleId;
        _reservation = reservation;
        _revocationToken = revocationToken;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        _revocationToken.ThrowIfCancellationRequested();
        return _inner.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        _revocationToken.ThrowIfCancellationRequested();
        return _inner.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        _revocationToken.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _revocationToken,
            cancellationToken);
        return await _inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _revocationToken.ThrowIfCancellationRequested();
        return _inner.Seek(offset, origin);
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeCoreAsync(async: false).AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await DisposeCoreAsync(async: true).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    internal void Revoke()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _inner.Dispose();
    }

    internal void ReleaseReservation() => _reservation.Release();

    private async ValueTask DisposeCoreAsync(bool async)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (async) await _inner.DisposeAsync().ConfigureAwait(false);
            else _inner.Dispose();
        }
        finally
        {
            await _owner.ReleaseAsync(this, _handleId).ConfigureAwait(false);
        }
    }
}
