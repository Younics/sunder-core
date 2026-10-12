using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Services;

/// <summary>A local or received content resource with automatically managed service-call transport.</summary>
/// <remarks>
/// Dispose content when finished with it, and dispose each stream returned by <see cref="OpenReadAsync"/>.
/// Received content remains bounded by its call deadline and the receiving service lifetime.
/// Sending local content copies its bytes; it does not transfer ownership of the local resource.
/// </remarks>
[JsonConverter(typeof(SunderRpcJsonConverterFactory))]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderContent : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<CancellationToken, ValueTask<Stream>> _open;
    private readonly Func<ValueTask>? _release;
    private readonly CancellationToken _lifetime;
    private readonly HashSet<SunderContentReadStream> _streams = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _opensDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _opening;
    private Task? _dispose;

    internal SunderContent(string mediaType, string fileName, long? length, string? sha256,
        DateTimeOffset? expiresAtUtc, bool isRepeatable, Func<CancellationToken, ValueTask<Stream>> open,
        Func<ValueTask>? release = null, CancellationToken lifetime = default)
    {
        MediaType = mediaType;
        FileName = fileName;
        Length = length;
        Sha256 = sha256;
        ExpiresAtUtc = expiresAtUtc;
        IsRepeatable = isRepeatable;
        _open = open;
        _release = release;
        _lifetime = lifetime;
    }

    /// <summary>Gets the media type.</summary>
    public string MediaType { get; }
    /// <summary>Gets the display-only file name.</summary>
    public string FileName { get; }
    /// <summary>Gets the byte length, or null for a local source whose length is not yet known.</summary>
    public long? Length { get; }
    /// <summary>Gets the lowercase SHA-256 for bytes or received content, or null for a local stream or file.</summary>
    public string? Sha256 { get; }
    /// <summary>Gets the received resource's expiry, or null for local content.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; }
    /// <summary>Gets whether repeated reads are supported, subject to the receiving host's use limit.</summary>
    public bool IsRepeatable { get; }

    /// <summary>Creates repeatable local content from a private copy of the supplied bytes.</summary>
    public static SunderContent FromBytes(ReadOnlyMemory<byte> bytes,
        string mediaType = "application/octet-stream", string fileName = "content.bin")
    {
        ValidateMetadata(mediaType, fileName, bytes.Length);
        var copy = bytes.ToArray();
        return new(mediaType, fileName, copy.Length, Convert.ToHexStringLower(SHA256.HashData(copy)),
            null, true, _ => ValueTask.FromResult<Stream>(new MemoryStream(copy, writable: false)));
    }

    /// <summary>Creates single-read local content from the stream's current position.</summary>
    /// <remarks>
    /// The content owns the input unless <paramref name="leaveOpen"/> is true, including when never read.
    /// Reading, sending, cancellation or disposal consumes the single read; the stream is never rewound.
    /// </remarks>
    public static SunderContent FromStream(Stream source, string mediaType = "application/octet-stream",
        string fileName = "content.bin", long? length = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("Content requires a readable stream.", nameof(source));
        ValidateMetadata(mediaType, fileName, length);
        var input = new StreamInput(source, leaveOpen);
        return new(mediaType, fileName, length, null, null, false, input.OpenAsync, input.DisposeAsync);
    }

    /// <summary>Creates repeatable local content from a file, opening it only when read or sent.</summary>
    /// <remarks>The content never owns or deletes the source file. The file must remain available while it is used.</remarks>
    public static SunderContent FromFile(string filePath, string mediaType = "application/octet-stream",
        string? fileName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var path = Path.GetFullPath(filePath);
        var name = fileName ?? Path.GetFileName(path);
        var length = new FileInfo(path).Length;
        ValidateMetadata(mediaType, name, length);
        return new(mediaType, name, length, null, null, true, _ => ValueTask.FromResult<Stream>(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan)));
    }

    /// <summary>Opens a read stream using this resource's local or receiving authority.</summary>
    /// <remarks>Cancellation applies to the returned stream's lifetime as well as opening it.</remarks>
    public async ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource linked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_dispose is not null, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token, _lifetime);
            _opening++;
        }
        Stream? stream = null;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            stream = await _open(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            var result = new SunderContentReadStream(stream, linked, RemoveStream);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_dispose is not null, this);
                _streams.Add(result);
            }
            result.ObserveCancellation();
            return result;
        }
        catch
        {
            try { if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false); }
            finally { linked.Dispose(); }
            throw;
        }
        finally
        {
            lock (_gate) if (--_opening == 0 && _dispose is not null) _opensDrained.TrySetResult();
        }
    }

    private void RemoveStream(SunderContentReadStream stream)
    {
        lock (_gate) _streams.Remove(stream);
    }

    internal void ThrowIfUnavailable()
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_dispose is not null, this);
        _lifetime.ThrowIfCancellationRequested();
    }

    /// <summary>Closes open reads and releases local input ownership or the received call-scope lease.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        SunderContentReadStream[] streams;
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispose = completion.Task;
            streams = _streams.ToArray();
            _streams.Clear();
            if (_opening == 0) _opensDrained.TrySetResult();
        }
        _ = DisposeCoreAsync(streams, completion);
        return new(completion.Task);
    }

    private async Task DisposeCoreAsync(SunderContentReadStream[] streams, TaskCompletionSource completion)
    {
        try
        {
            try
            {
                try { await _stopping.CancelAsync().ConfigureAwait(false); }
                finally
                {
                    try { await Task.WhenAll(streams.Select(static stream => stream.DisposeAsync().AsTask())).ConfigureAwait(false); }
                    finally { await _opensDrained.Task.ConfigureAwait(false); }
                }
            }
            finally
            {
                try { if (_release is not null) await _release().ConfigureAwait(false); }
                finally { _stopping.Dispose(); }
            }
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    private static void ValidateMetadata(string mediaType, string fileName, long? length)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (mediaType.Length is < 1 or > 256 || mediaType.Any(static character => character > 0x7f))
            throw new ArgumentException("The media type must contain 1–256 ASCII characters.", nameof(mediaType));
        if (fileName.EnumerateRunes().Count() > 1024)
            throw new ArgumentException("The display file name exceeds 1024 characters.", nameof(fileName));
        if (length is < 0 or > 9_007_199_254_740_991)
            throw new ArgumentOutOfRangeException(nameof(length));
    }

    private sealed class StreamInput(Stream source, bool leaveOpen) : IAsyncDisposable
    {
        private int _opened;
        private int _disposed;

        public ValueTask<Stream> OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Interlocked.Exchange(ref _opened, 1) != 0)
                throw new InvalidOperationException("Stream content can be opened or sent only once.");
            // The shared release makes disposing a read and disposing the content idempotent for its input.
            return ValueTask.FromResult<Stream>(new SunderContentReadStream(source,
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken), _ => { }, DisposeAsync));
        }

        public ValueTask DisposeAsync()
            => Interlocked.Exchange(ref _disposed, 1) == 0 && !leaveOpen ? source.DisposeAsync() : ValueTask.CompletedTask;
    }
}
