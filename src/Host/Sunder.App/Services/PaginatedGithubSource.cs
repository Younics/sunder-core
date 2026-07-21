using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Velopack.Sources;

namespace Sunder.App.Services;

internal sealed class PaginatedGithubSource : GithubSource
{
    private const int PageSize = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;
    private readonly string _releasesApiUrl;

    public PaginatedGithubSource(
        string repositoryUrl,
        string? accessToken,
        bool prerelease,
        HttpMessageHandler? handler = null)
        : base(repositoryUrl, accessToken, prerelease)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repository)
            || !string.Equals(repository.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(repository.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The Sunder update source must be a github.com repository URL.", nameof(repositoryUrl));
        }
        var segments = repository.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
        {
            throw new ArgumentException("The Sunder update source must identify one GitHub owner and repository.", nameof(repositoryUrl));
        }
        var repositoryName = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];
        _releasesApiUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(segments[0])}/{Uri.EscapeDataString(repositoryName)}/releases";
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("sunder-app", SunderAppVersion.CurrentText));
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
    }

    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        var releases = new List<GithubRelease>();
        for (var page = 1; ; page++)
        {
            var response = await _client.GetStringAsync(
                $"{_releasesApiUrl}?per_page={PageSize}&page={page}").ConfigureAwait(false);
            var pageItems = JsonSerializer.Deserialize<GitHubReleasePageItem[]>(response, JsonOptions)
                            ?? throw new InvalidDataException("GitHub returned an invalid release list.");
            releases.AddRange(pageItems
                .Where(item => !item.Draft && (includePrereleases || !item.Prerelease))
                .Select(item => new GithubRelease
                {
                    Name = item.Name,
                    Prerelease = item.Prerelease,
                    PublishedAt = item.PublishedAt,
                    Assets = item.Assets.Select(asset => new GithubReleaseAsset
                    {
                        Name = asset.Name,
                        ContentType = asset.ContentType,
                        Url = asset.Url,
                        BrowserDownloadUrl = asset.BrowserDownloadUrl,
                    }).ToArray(),
                }));
            if (pageItems.Length < PageSize)
            {
                break;
            }
        }

        return releases
            .OrderByDescending(release => release.PublishedAt)
            .ToArray();
    }

    internal Task<GithubRelease[]> GetReleasesForTestsAsync(bool includePrereleases)
        => GetReleases(includePrereleases);

    private sealed record GitHubReleasePageItem(
        string? Name,
        bool Draft,
        bool Prerelease,
        [property: JsonPropertyName("published_at")] DateTime? PublishedAt,
        GitHubReleasePageAsset[] Assets);

    private sealed record GitHubReleasePageAsset(
        string? Name,
        [property: JsonPropertyName("content_type")] string? ContentType,
        string? Url,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);
}
