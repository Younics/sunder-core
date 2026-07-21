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
}
