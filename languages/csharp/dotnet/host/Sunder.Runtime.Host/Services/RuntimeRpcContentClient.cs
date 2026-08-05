using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeRpcInvocationAuthority(
    RuntimeContentTransferStore store,
    PackageSessionState sessions,
    RuntimeRpcCatalog catalog,
    RuntimeTransportPolicyOptions policy,
    string callerPackageId,
    string providerPackageId,
    long sessionGeneration,
    RuntimeRpcContentEndpoint providerEndpoint,
    DateTimeOffset deadlineUtc,
    RuntimeRpcContentAuthority contentAuthority,
    bool ownsContentAuthority,
    CancellationToken invocationCancellation,
    TimeProvider? timeProvider = null) : ISunderRpcInvocationAuthority
{
    private readonly CancellationTokenSource _revocation =
        CancellationTokenSource.CreateLinkedTokenSource(invocationCancellation);
    private readonly CancellationTokenRegistration _contentRetirement = ownsContentAuthority
        ? invocationCancellation.UnsafeRegister(
            static state =>
            {
                var (contentStore, authority) =
                    ((RuntimeContentTransferStore, RuntimeRpcContentAuthority))state!;
                contentStore.DiscardRpcContentAuthority(authority);
            },
            (store, contentAuthority))
        : default;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private int _revoked;

    public CancellationToken RevocationToken => _revocation.Token;

    public async ValueTask<SunderRpcContentReference> RegisterContentAsync(
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        EnsureActive();
        ValidateActivation();
        ValidateOptions(options);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _revocation.Token,
            cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var expiresAt = Min(
            options.ExpiresAtUtc ?? now + policy.ContentTransferLifetime,
            deadlineUtc,
            now + policy.ContentTransferLifetime);
        try
        {
            return await store.RegisterRpcContentAsync(
                source,
                options.Length,
                options.MediaType,
                options.FileName,
                providerPackageId,
                callerPackageId,
                sessionGeneration,
                providerEndpoint,
                expiresAt,
                options.Repeatability,
                options.MaximumUses,
                linked.Token,
                contentAuthority).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_revocation.IsCancellationRequested)
        {
            throw Revoked();
        }
    }

    public async ValueTask<SunderRpcContentReference> RegisterContentFileAsync(
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        EnsureActive();
        await using var stream = new FileStream(
            Path.GetFullPath(filePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await RegisterContentAsync(stream, options, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<Stream> OpenContentAsync(
        SunderRpcContentReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureActive();
        ValidateActivation();
        var lease = store.AcquireRpcContent(
            reference,
            callerPackageId,
            providerPackageId,
            sessionGeneration,
            providerEndpoint,
            contentAuthority)
            ?? throw SunderRpcException.Infrastructure(
                SunderRpcErrorKind.NotFound,
                "rpc.content.unavailable",
                "The RPC content reference is stale, exhausted, or unavailable to this invocation.");
        try
        {
            Stream stream = new RuntimeRpcContentReadStream(
                lease.Content,
                () => store.ReleaseRpcContent(lease),
                _revocation.Token,
                () =>
                {
                    EnsureActive();
                    ValidateActivation();
                });
            return ValueTask.FromResult(stream);
        }
        catch
        {
            store.ReleaseRpcContent(lease);
            throw;
        }
    }

    public void Revoke()
    {
        if (Interlocked.Exchange(ref _revoked, 1) != 0) return;
        _contentRetirement.Dispose();
        if (ownsContentAuthority)
        {
            store.DiscardRpcContentAuthority(contentAuthority);
        }
        var callbacks = RuntimeCancellation.Signal(_revocation);
        RuntimeCancellation.DisposeAfterCallbacks(_revocation, callbacks);
    }

    private void EnsureActive()
    {
        if (Volatile.Read(ref _revoked) != 0 || _revocation.IsCancellationRequested) throw Revoked();
    }

    private void ValidateActivation()
    {
        using var lease = sessions.AcquireLease();
        if (lease.Generation != sessionGeneration
            || sessions.GetLoadedPackage(lease, providerPackageId)?.RuntimeActivationId != providerEndpoint.ActivationId
            || !catalog.TryGetActiveEndpoint(
                new SunderRpcEndpointReference(providerEndpoint.EndpointReference),
                out var provider,
                out _)
            || provider?.Snapshot.ActivationId != providerEndpoint.ActivationId
            || !string.Equals(
                provider.Snapshot.Endpoint.Value,
                providerEndpoint.EndpointReference,
                StringComparison.Ordinal))
        {
            throw SunderRpcException.Infrastructure(
                SunderRpcErrorKind.StaleEndpoint,
                "rpc.content.activation-stale",
                "The RPC content authority identifies a stale provider activation.");
        }
    }

    internal static void ValidateOptions(SunderRpcContentRegistrationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.MediaType)
            || string.IsNullOrWhiteSpace(options.FileName)
            || options.Length is < 0
            || options.MaximumUses < 1
            || options.Repeatability == SunderRpcContentRepeatability.SingleUse
            && options.MaximumUses != 1)
        {
            throw new ArgumentException("The RPC content registration options are invalid.", nameof(options));
        }
    }

    internal static DateTimeOffset Min(params DateTimeOffset[] values)
        => values.Min();

    private static SunderRpcException Revoked()
        => SunderRpcException.Infrastructure(
            SunderRpcErrorKind.Unavailable,
            "rpc.content.authority-revoked",
            "The RPC invocation content authority has ended.");
}

internal sealed class RuntimeRpcContentReadStream : Stream
{
    private readonly Stream _inner;
    private readonly CancellationToken _revocationToken;
    private readonly Action? _validate;
    private CancellationTokenRegistration _revocationRegistration;
    private Action? _release;
    private int _disposed;

    public RuntimeRpcContentReadStream(
        Stream inner,
        Action release,
        CancellationToken revocationToken = default,
        Action? validate = null)
    {
        _inner = inner;
        _release = release;
        _revocationToken = revocationToken;
        _validate = validate;
        if (revocationToken.CanBeCanceled)
        {
            _revocationRegistration = revocationToken.UnsafeRegister(
                static state => ((RuntimeRpcContentReadStream)state!).Dispose(),
                this);
        }
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
        _validate?.Invoke();
        return _inner.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        _revocationToken.ThrowIfCancellationRequested();
        _validate?.Invoke();
        return _inner.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        _revocationToken.ThrowIfCancellationRequested();
        _validate?.Invoke();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _revocationToken,
            cancellationToken);
        return await _inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _revocationToken.ThrowIfCancellationRequested();
        _validate?.Invoke();
        return _inner.Seek(offset, origin);
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCore();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _revocationRegistration.Unregister();
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _release, null)?.Invoke();
            }
        }
        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _revocationRegistration.Unregister();
        try
        {
            _inner.Dispose();
        }
        finally
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}
