using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Sunder.App.Services;
using Sunder.Registry.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RegistryApiClientTests
{
    private const int MaxJsonResponseBytes = 4 * 1024 * 1024;

    [Fact]
    public async Task SearchAsync_UsesInjectedHttpClientAndRegistryUrl()
    {
        var package = new RegistryPackageSummary(
            "younics.ai.agent",
            "AI Agent",
            "Adds local agents.",
            "1.0.0",
            IconUrl: null,
            IsYanked: false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create<IReadOnlyList<RegistryPackageSummary>>([package]),
        });
        using var httpClient = new HttpClient(handler);
        using var registryClient = new RegistryApiClient(new Uri("https://registry.example/"), httpClient);

        var results = await registryClient.SearchAsync(" agent package ", skip: 10, take: 20);

        Assert.Collection(results, result => Assert.Equal("younics.ai.agent", result.PackageId));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri("https://registry.example/api/v1/packages?skip=10&take=20&sort=downloads&query=agent%20package"), request.RequestUri);
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeInjectedHttpClient()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        var registryClient = new RegistryApiClient(new Uri("https://registry.example/"), httpClient);

        registryClient.Dispose();

        using var response = await httpClient.GetAsync(new Uri("https://registry.example/health"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetVersionAsync_RemainsAHistoricalDetailRequest()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler);
        using var registryClient = new RegistryApiClient(new Uri("https://registry.example/"), httpClient);

        var result = await registryClient.GetVersionAsync("younics.ai.agent", "2.0.0");

        Assert.Null(result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            new Uri("https://registry.example/api/v1/packages/younics.ai.agent/versions/2.0.0"),
            request.RequestUri);
        Assert.Equal(string.Empty, request.RequestUri?.Query);
    }

    [Fact]
    public async Task DownloadStackAsync_WhenVerificationFails_PreservesExistingDestinationAndDeletesTemporaryFile()
    {
        var downloaded = "corrupt download"u8.ToArray();
        var expectedHash = Convert.ToHexString(SHA256.HashData("expected download"u8)).ToLowerInvariant();
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(downloaded),
        });
        using var httpClient = new HttpClient(handler);
        using var registryClient = new RegistryApiClient(new Uri("https://registry.example/"), httpClient);
        var root = Path.Combine(Path.GetTempPath(), "sunder-registry-download-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "stack.sunderstack");
        await File.WriteAllTextAsync(destination, "existing valid content");

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => registryClient.DownloadStackAsync(
                new RegistryStackArtifact(expectedHash, downloaded.Length, "/artifacts/stack"),
                "stack",
                destination));

            Assert.Equal("existing valid content", await File.ReadAllTextAsync(destination));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(root),
                path => Path.GetFileName(path).Contains(".download-", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadStackAsync_WhenVerificationSucceeds_PublishesDestination()
    {
        var downloaded = "verified stack"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(downloaded)).ToLowerInvariant();
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(downloaded),
        });
        using var httpClient = new HttpClient(handler);
        using var registryClient = new RegistryApiClient(new Uri("https://registry.example/"), httpClient);
        var root = Path.Combine(Path.GetTempPath(), "sunder-registry-download-tests", Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(root, "stack.sunderstack");

        try
        {
            await registryClient.DownloadStackAsync(
                new RegistryStackArtifact(hash, downloaded.Length, "https://registry.example/stack.sunderstack"),
                "stack",
                destination);

            Assert.Equal(downloaded, await File.ReadAllBytesAsync(destination));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(root),
                path => Path.GetFileName(path).Contains(".download-", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadStackAsync_UsesIndependentHeaderAndTransferDeadlines()
    {
        var downloaded = "verified stack"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(downloaded)).ToLowerInvariant();
        using var headerDeadline = new CancellationTokenSource();
        using var transferDeadline = new CancellationTokenSource();
        var timeoutDurations = new List<TimeSpan>();
        var timeoutIndex = 0;
        CancellationTokenSource CreateTimeout(TimeSpan duration)
        {
            timeoutDurations.Add(duration);
            return CancellationTokenSource.CreateLinkedTokenSource(
                (timeoutIndex++ == 0 ? headerDeadline : transferDeadline).Token);
        }

        var body = new TrackingReadStream(downloaded, headerDeadline.Cancel);
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body),
        });
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var registryClient = new RegistryApiClient(
            new Uri("https://registry.example/"),
            httpClient,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(15),
            CreateTimeout);
        var root = Path.Combine(Path.GetTempPath(), "sunder-registry-download-tests", Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(root, "stack.sunderstack");

        try
        {
            await registryClient.DownloadStackAsync(
                new RegistryStackArtifact(hash, downloaded.Length, "/artifacts/stack"),
                "stack",
                destination);

            Assert.Equal(downloaded, await File.ReadAllBytesAsync(destination));
            Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15)], timeoutDurations);
            Assert.True(headerDeadline.IsCancellationRequested);
            Assert.False(transferDeadline.IsCancellationRequested);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadStackAsync_SeparatesTransferTimeoutFromCallerCancellation(bool transferTimesOut)
    {
        var downloaded = "verified stack"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(downloaded)).ToLowerInvariant();
        using var headerDeadline = new CancellationTokenSource();
        using var transferDeadline = new CancellationTokenSource();
        using var callerCancellation = new CancellationTokenSource();
        var timeoutIndex = 0;
        CancellationTokenSource CreateTimeout(TimeSpan _)
            => CancellationTokenSource.CreateLinkedTokenSource(
                (timeoutIndex++ == 0 ? headerDeadline : transferDeadline).Token);

        var body = new TrackingReadStream(
            downloaded,
            transferTimesOut ? transferDeadline.Cancel : callerCancellation.Cancel);
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body),
        });
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var registryClient = new RegistryApiClient(
            new Uri("https://registry.example/"),
            httpClient,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(15),
            CreateTimeout);
        var root = Path.Combine(Path.GetTempPath(), "sunder-registry-download-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "stack.sunderstack");

        try
        {
            var exception = await Record.ExceptionAsync(() => registryClient.DownloadStackAsync(
                new RegistryStackArtifact(
                    hash,
                    downloaded.Length,
                    "https://registry.example/artifacts/stack"),
                "stack",
                destination,
                callerCancellation.Token));

            if (transferTimesOut)
            {
                Assert.True(exception is TimeoutException, exception?.ToString());
                var timeout = (TimeoutException)exception!;
                Assert.Contains("Registry Stack transfer", timeout.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.True(exception is OperationCanceledException, exception?.ToString());
            }
            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadStackAsync_WhenBodyExceedsDeclaredSize_DoesNotPublishDestination()
    {
        var downloaded = "four"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(downloaded)).ToLowerInvariant();
        var content = new StreamContent(new MemoryStream(downloaded));
        content.Headers.ContentLength = null;
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });
        using var httpClient = new HttpClient(handler);
        using var registryClient = new RegistryApiClient(new Uri("https://registry.example/"), httpClient);
        var root = Path.Combine(Path.GetTempPath(), "sunder-registry-download-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "stack.sunderstack");

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => registryClient.DownloadStackAsync(
                new RegistryStackArtifact(hash, 3, "/artifacts/stack"),
                "stack",
                destination));

            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("file:///tmp/stack.sunderstack")]
    [InlineData("ftp://registry.example/stack.sunderstack")]
    [InlineData("https://cdn.example/stack.sunderstack")]
    public async Task DownloadStackAsync_RejectsUntrustedUrlsBeforeSending(string url)
    {
        var handler = new RecordingHttpMessageHandler(_ => throw new InvalidOperationException("Request must not be sent."));
        using var httpClient = new HttpClient(handler);
        using var registryClient = new RegistryApiClient(new Uri("https://registry.example/"), httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(() => registryClient.DownloadStackAsync(
            new RegistryStackArtifact(new string('0', 64), 1, url),
            "stack",
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sunderstack")));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SearchAsync_StopsChunkedBodyAtLimitPlusOneAndDisposesStream()
    {
        var body = new TrackingReadStream(new byte[MaxJsonResponseBytes + 128]);
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body),
        });
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var registryClient = new RegistryApiClient(new Uri("https://registry.example/"), httpClient);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            registryClient.SearchAsync(null, skip: 0, take: 20));

        Assert.Contains("byte limit", exception.Message, StringComparison.Ordinal);
        Assert.Equal(MaxJsonResponseBytes + 1, body.BytesRead);
        Assert.True(body.IsDisposed);
    }

    [Fact]
    public async Task SearchAsync_AcceptsChunkedBodyAtExactLimit()
    {
        var payload = Enumerable.Repeat((byte)' ', MaxJsonResponseBytes).ToArray();
        payload[0] = (byte)'[';
        payload[1] = (byte)']';
        var body = new TrackingReadStream(payload);
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body),
        });
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var registryClient = new RegistryApiClient(new Uri("https://registry.example/"), httpClient);

        var result = await registryClient.SearchAsync(null, skip: 0, take: 20);

        Assert.Empty(result);
        Assert.Equal(MaxJsonResponseBytes, body.BytesRead);
        Assert.True(body.IsDisposed);
    }

    [Fact]
    public async Task SearchAsync_TimeoutAndCallerCancellationDisposeBodyStreams()
    {
        var timedOutBody = new CancellationBlockingStream();
        var timeoutHandler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(timedOutBody),
        });
        using var timeoutClient = new HttpClient(timeoutHandler) { Timeout = Timeout.InfiniteTimeSpan };
        using var timedClient = new RegistryApiClient(
            new Uri("https://registry.example/"),
            timeoutClient,
            TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => timedClient.SearchAsync(null, 0, 20));
        Assert.True(timedOutBody.IsDisposed);

        var cancelledBody = new CancellationBlockingStream();
        var cancellationHandler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(cancelledBody),
        });
        using var cancellationClient = new HttpClient(cancellationHandler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cancelledClient = new RegistryApiClient(new Uri("https://registry.example/"), cancellationClient);
        using var cancellation = new CancellationTokenSource();
        var pending = cancelledClient.SearchAsync(null, 0, 20, cancellationToken: cancellation.Token);
        await cancelledBody.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(cancelledBody.IsDisposed);
    }

    [Fact]
    public async Task SearchAsync_RejectsResponseFromChangedOrigin()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://other.example/api/v1/packages"),
            Content = JsonContent.Create(Array.Empty<RegistryPackageSummary>()),
        });
        using var httpClient = new HttpClient(handler);
        using var registryClient = new RegistryApiClient(new Uri("https://registry.example/"), httpClient);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            registryClient.SearchAsync(null, 0, 20));

        Assert.Contains("untrusted origin", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        private readonly List<RecordedRequest> _requests = [];

        public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri));
            var response = send(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? RequestUri);

    private sealed class TrackingReadStream(byte[] content, Action? beforeRead = null) : Stream
    {
        private int _offset;
        private int _disposed;
        private Action? _beforeRead = beforeRead;
        public int BytesRead => Volatile.Read(ref _offset);
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            Interlocked.Exchange(ref _beforeRead, null)?.Invoke();
            var count = Math.Min(buffer.Length, content.Length - _offset);
            content.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Exchange(ref _beforeRead, null)?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, content.Length - _offset);
            content.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;
            return ValueTask.FromResult(count);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) Volatile.Write(ref _disposed, 1);
            base.Dispose(disposing);
        }
    }

    private sealed class CancellationBlockingStream : Stream
    {
        private int _disposed;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) Volatile.Write(ref _disposed, 1);
            base.Dispose(disposing);
        }
    }
}
