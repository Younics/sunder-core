using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

/// <summary>Owns one immutable upload until cancellation or consumption, independently of any window.</summary>
internal sealed class PackageUploadRetentionSession : IAsyncDisposable
{
    internal static readonly TimeSpan RenewalInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly IRuntimePackageUploadRetentionClient _client;
    private readonly CancellationTokenSource _pending;
    private readonly CancellationTokenSource _renewal;
    private readonly Task _renewing;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private Exception? _failure;

    private PackageUploadRetentionSession(IRuntimePackageUploadRetentionClient client,
        RuntimePackageUploadRetentionHandle handle, CancellationToken ownerToken, TimeProvider clock)
    {
        _client = client;
        Handle = handle;
        _pending = CancellationTokenSource.CreateLinkedTokenSource(ownerToken);
        _renewal = CancellationTokenSource.CreateLinkedTokenSource(_pending.Token);
        Token = _pending.Token;
        _renewing = RenewAsync(clock, _renewal.Token);
    }

    public RuntimePackageUploadRetentionHandle Handle { get; }
    public CancellationToken Token { get; }
    public Exception? Failure => Volatile.Read(ref _failure);

    public static async Task<PackageUploadRetentionSession> AcquireAsync(
        IRuntimePackageUploadRetentionClient client, ContentUploadDescriptor upload, CancellationToken token,
        TimeProvider? clock = null)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout);
        RuntimePackageUploadRetentionResult result;
        try
        {
            result = await client.RetainPackageUploadAsync(
                new(upload.UploadId, upload.ContentHash, upload.Length), deadline.Token).ConfigureAwait(false);
        }
        catch (RuntimeClientException error)
        {
            throw Unavailable(error.Message, error.ErrorCode);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw Unavailable("The upload retention request timed out.", RuntimePackageUploadRetentionErrorCodes.Unavailable);
        }
        var handle = result.Retention?.Handle;
        if (!result.Success || handle is null)
            throw Unavailable(result.Message, result.ErrorCode);
        if (handle.UploadId != upload.UploadId || handle.ContentHash != upload.ContentHash || handle.Length != upload.Length
            || string.IsNullOrWhiteSpace(handle.LeaseId))
            throw Unavailable("The returned upload proof does not match the reviewed archive.", RuntimePackageUploadRetentionErrorCodes.Conflict);

        var session = new PackageUploadRetentionSession(client, handle, token, clock ?? TimeProvider.System);
        if (token.IsCancellationRequested)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        return session;
    }

    public async Task CheckAvailableAsync(CancellationToken token)
    {
        ThrowIfUnavailable();
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token, token);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            var result = await _client.GetPackageUploadRetentionAsync(Handle, deadline.Token).ConfigureAwait(false);
            Validate(result);
            ThrowIfUnavailable();
        }
        catch (RuntimeClientException error)
        {
            throw Unavailable(error.Message, error.ErrorCode);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !Token.IsCancellationRequested)
        {
            throw Unavailable("Upload availability could not be confirmed in time.", RuntimePackageUploadRetentionErrorCodes.Unavailable);
        }
    }

    public async Task PrepareForConsumptionAsync(CancellationToken token)
    {
        // Stop renewal before stage consumes the upload. A racing Gone response must not cancel a commit.
        _renewal.Cancel();
        await _renewing.ConfigureAwait(false);
        ThrowIfUnavailable();
        await CheckAvailableAsync(token).ConfigureAwait(false);
    }

    public void ThrowIfUnavailable()
    {
        if (Failure is { } failure) throw failure;
        Token.ThrowIfCancellationRequested();
    }

    private async Task RenewAsync(TimeProvider clock, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(RenewalInterval, clock, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(RequestTimeout);
                var result = await _client.RenewPackageUploadRetentionAsync(Handle, deadline.Token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                Validate(result);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            var failure = error switch
            {
                PackageUploadRetentionException retainedError => retainedError,
                RuntimeClientException runtimeError => Unavailable(runtimeError.Message, runtimeError.ErrorCode),
                _ => Unavailable(error.Message, RuntimePackageUploadRetentionErrorCodes.Unavailable),
            };
            Interlocked.CompareExchange(ref _failure, failure, null);
            _pending.Cancel(); // Cancels the runtime/capability prompt and pending navigation as well.
        }
    }

    private void Validate(RuntimePackageUploadRetentionResult result)
    {
        if (!result.Success || result.Retention is null)
            throw Unavailable(result.Message, result.ErrorCode);
        if (result.Retention.Handle != Handle)
            throw Unavailable("The upload retention identity changed.", RuntimePackageUploadRetentionErrorCodes.Conflict);
        // UTC deadlines are display data. Availability and the absolute cap are Host/continuous-clock decisions.
    }

    private static PackageUploadRetentionException Unavailable(string message, string? code)
        => new($"The pending package upload is no longer available. Select the original package file again to upload it and review its runtimes and permissions. {message} ({code ?? RuntimePackageUploadRetentionErrorCodes.Unavailable})");

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _renewal.Cancel();
        await _renewing.ConfigureAwait(false);
        _pending.Cancel();
        try
        {
            // Use a fresh bounded cleanup token: cancellation/consumption must still release the proof.
            using var deadline = new CancellationTokenSource(RequestTimeout);
            var result = await _client.ReleasePackageUploadRetentionAsync(Handle, deadline.Token).ConfigureAwait(false);
            if (!result.Success) AppSessionLog.WriteInfo($"Package upload retention cleanup was not acknowledged: {result.ErrorCode}.");
        }
        catch (Exception error)
        {
            AppSessionLog.WriteError("Package upload retention cleanup was unavailable; Host expiry will clean the upload.", error);
        }
        finally
        {
            _renewal.Dispose();
            _pending.Dispose();
        }
    }

    private sealed class PackageUploadRetentionException(string message) : InvalidOperationException(message);
}
