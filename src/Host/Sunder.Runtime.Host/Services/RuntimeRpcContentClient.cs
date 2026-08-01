using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeRpcContentClient(
    RuntimeContentTransferStore store,
    PackageSessionState sessions,
    RuntimeTransportPolicyOptions policy,
    string packageId,
    Guid activationId,
    TimeProvider? timeProvider = null) : ISunderRpcContentClient
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<SunderRpcContentReference> RegisterAsync(
        SunderRpcInvocationContext context,
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        var generation = ValidateContext(context);
        ValidateOptions(options);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken,
            cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var expiresAt = Min(
            options.ExpiresAtUtc ?? now + policy.ContentTransferLifetime,
            context.DeadlineUtc,
            now + policy.ContentTransferLifetime);
        return await store.RegisterRpcContentAsync(
            source,
            options.Length,
            options.MediaType,
            options.FileName,
            packageId,
            context.CallerPackageId,
            generation,
            expiresAt,
            options.Repeatability,
            options.MaximumUses,
            linked.Token).ConfigureAwait(false);
    }

    public async ValueTask<SunderRpcContentReference> RegisterFileAsync(
        SunderRpcInvocationContext context,
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using var stream = new FileStream(
            Path.GetFullPath(filePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await RegisterAsync(context, stream, options, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<Stream> OpenReadAsync(
        SunderRpcInvocationContext context,
        SunderRpcContentReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        context.CancellationToken.ThrowIfCancellationRequested();
        var generation = ValidateContext(context);
        var lease = store.AcquireRpcContent(
            reference,
            context.CallerPackageId,
            packageId,
            generation)
            ?? throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.NotFound,
                "rpc.content.unavailable",
                "The RPC content reference is stale, exhausted, or unavailable to this provider activation."));
        try
        {
            Stream stream = new RuntimeRpcContentReadStream(
                new FileStream(
                    lease.FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan),
                () => store.ReleaseRpcContent(lease));
            return ValueTask.FromResult(stream);
        }
        catch
        {
            store.ReleaseRpcContent(lease);
            throw;
        }
    }

    private long ValidateContext(SunderRpcInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(context.Provider.PackageId, packageId, StringComparison.Ordinal)
            || context.Provider.ActivationId != activationId
            || context.Provider.State != SunderRpcProviderState.Active)
        {
            throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.StaleEndpoint,
                "rpc.content.activation-mismatch",
                "The RPC content operation does not match this provider activation."));
        }

        using var lease = sessions.AcquireLease();
        if (lease.Generation != context.Provider.SessionGeneration
            || sessions.GetLoadedPackage(lease, packageId)?.RuntimeActivationId != activationId)
        {
            throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.StaleEndpoint,
                "rpc.content.generation-stale",
                "The RPC content operation identifies a stale Runtime generation."));
        }
        return lease.Generation;
    }

    private static void ValidateOptions(SunderRpcContentRegistrationOptions options)
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

    private static DateTimeOffset Min(params DateTimeOffset[] values)
        => values.Min();
}

internal sealed class RuntimeRpcContentReadStream(Stream inner, Action release) : Stream
{
    private Action? _release = release;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _release, null)?.Invoke();
        GC.SuppressFinalize(this);
    }
}

internal sealed class UnavailableRuntimeRpcContentClient : ISunderRpcContentClient
{
    public static UnavailableRuntimeRpcContentClient Instance { get; } = new();

    private UnavailableRuntimeRpcContentClient()
    {
    }

    public ValueTask<SunderRpcContentReference> RegisterAsync(
        SunderRpcInvocationContext context,
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException<SunderRpcContentReference>(Unavailable());

    public ValueTask<SunderRpcContentReference> RegisterFileAsync(
        SunderRpcInvocationContext context,
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException<SunderRpcContentReference>(Unavailable());

    public ValueTask<Stream> OpenReadAsync(
        SunderRpcInvocationContext context,
        SunderRpcContentReference reference,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException<Stream>(Unavailable());

    private static SunderRpcException Unavailable()
        => new(new SunderRpcError(
            SunderRpcErrorKind.Unavailable,
            "rpc.content.host-unavailable",
            "Host-mediated RPC content transfer is unavailable in this Runtime context."));
}
