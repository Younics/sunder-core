using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Sunder.App.Services;
using Sunder.Registry.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RegistryApiClientTests
{
    [Fact]
    public async Task SearchAsync_UsesInjectedHttpClientAndRegistryUrl()
    {
        var package = new RegistryPackageSummary(
            "sunder.package.agent",
            "Sunder Agent",
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

        Assert.Collection(results, result => Assert.Equal("sunder.package.agent", result.PackageId));
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
}
