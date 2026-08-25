using System.Net;
using System.Text;
using System.Text.Json;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PaginatedGithubSourceTests
{
    [Fact]
    public async Task GetReleasesAsync_PaginatesPastUnrelatedFirstPageAndExcludesDrafts()
    {
        var requests = new List<Uri>();
        var firstPage = Enumerable.Range(0, 100)
            .Select(index => CreateRelease($"unrelated-{index}", draft: false))
            .ToArray();
        var secondPage = new[]
        {
            CreateRelease("matching-app-release", draft: false),
            CreateRelease("stale-draft", draft: true),
        };
        using var handler = new RecordingHandler(request =>
        {
            requests.Add(request.RequestUri!);
            var page = request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal) ? secondPage : firstPage;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(page), Encoding.UTF8, "application/json"),
            };
        });
        var source = new PaginatedGithubSource(
            "https://github.com/Younics/sunder-core",
            accessToken: null,
            prerelease: false,
            handler);

        var releases = await source.GetReleasesForTestsAsync(includePrereleases: false);

        Assert.Equal(2, requests.Count);
        Assert.Equal(101, releases.Length);
        Assert.Contains(releases, release => release.Name == "matching-app-release");
        Assert.DoesNotContain(releases, release => release.Name == "stale-draft");
    }

    [Fact]
    public async Task GetReleasesAsync_RejectsOversizedPageCollection()
    {
        var page = Enumerable.Range(0, 101)
            .Select(index => CreateRelease($"release-{index}", draft: false))
            .ToArray();
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(page), Encoding.UTF8, "application/json"),
        });
        var source = new PaginatedGithubSource(
            "https://github.com/Younics/sunder-core",
            accessToken: null,
            prerelease: false,
            handler);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.GetReleasesForTestsAsync(includePrereleases: false));

        Assert.Contains("bounded release page", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetReleasesAsync_RejectsResponseFromChangedOrigin()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://other.example/releases"),
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        });
        var source = new PaginatedGithubSource(
            "https://github.com/Younics/sunder-core",
            accessToken: "test-token",
            prerelease: false,
            handler);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.GetReleasesForTestsAsync(includePrereleases: false));

        Assert.Contains("untrusted origin", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetReleasesAsync_RejectsRedirectResponses()
    {
        using var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://other.example/releases");
            return response;
        });
        var source = new PaginatedGithubSource(
            "https://github.com/Younics/sunder-core",
            accessToken: "test-token",
            prerelease: false,
            handler);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.GetReleasesForTestsAsync(includePrereleases: false));

        Assert.Contains("redirects are not allowed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetReleasesAsync_TimeoutDisposesResponseStream()
    {
        var body = new CancellationBlockingStream();
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body),
        });
        var source = new PaginatedGithubSource(
            "https://github.com/Younics/sunder-core",
            accessToken: null,
            prerelease: false,
            handler,
            requestTimeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            source.GetReleasesForTestsAsync(includePrereleases: false));

        Assert.True(body.IsDisposed);
    }

    private static object CreateRelease(string name, bool draft)
        => new
        {
            name,
            draft,
            prerelease = false,
            published_at = DateTime.UtcNow,
            assets = new[]
            {
                new
                {
                    name = "releases.app-win-x64-stable.json",
                    content_type = "application/json",
                    url = "https://api.github.test/asset",
                    browser_download_url = "https://github.test/asset",
                },
            },
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }

    private sealed class CancellationBlockingStream : Stream
    {
        private int _disposed;
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
