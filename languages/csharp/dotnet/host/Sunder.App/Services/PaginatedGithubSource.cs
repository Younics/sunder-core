using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Velopack.Sources;

namespace Sunder.App.Services;

internal sealed class PaginatedGithubSource : GithubSource
{
    private const int PageSize = 100;
    private const int MaximumPages = 20;
    private const int MaximumAssetsPerRelease = 256;
    private const long MaximumPageBytes = 4L * 1024 * 1024;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly Uri ApiOrigin = new("https://api.github.com/");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
    };
    private readonly HttpClient _client;
    private readonly string _releasesApiUrl;
    private readonly TimeSpan _requestTimeout;

    public PaginatedGithubSource(
        string repositoryUrl,
        string? accessToken,
        bool prerelease,
        HttpMessageHandler? handler = null,
        TimeSpan? requestTimeout = null)
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
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (_requestTimeout <= TimeSpan.Zero && _requestTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }
        _client = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: true);
        _client.Timeout = Timeout.InfiniteTimeSpan;
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
        => await GetReleasesAsync(includePrereleases, CancellationToken.None).ConfigureAwait(false);

    private async Task<GithubRelease[]> GetReleasesAsync(
        bool includePrereleases,
        CancellationToken cancellationToken)
    {
        var releases = new List<GithubRelease>();
        for (var page = 1; page <= MaximumPages + 1; page++)
        {
            var pageItems = await GetPageAsync(page, cancellationToken).ConfigureAwait(false);
            if (pageItems.Length > PageSize
                || pageItems.Any(item => item is null
                                         || item.Assets is null
                                         || item.Assets.Length > MaximumAssetsPerRelease
                                         || item.Assets.Any(asset => asset is null)))
            {
                throw new InvalidDataException("GitHub returned an invalid bounded release page.");
            }
            if (page > MaximumPages)
            {
                if (pageItems.Length == 0)
                {
                    return releases
                        .OrderByDescending(release => release.PublishedAt)
                        .ToArray();
                }
                break;
            }
            releases.AddRange(pageItems
                .Where(item => !item.Draft && (includePrereleases || !item.Prerelease))
                .Select(item => new GithubRelease
                {
                    Name = item.Name,
                    Prerelease = item.Prerelease,
                    PublishedAt = item.PublishedAt,
                    Assets = item.Assets!.Select(asset => new GithubReleaseAsset
                    {
                        Name = asset.Name,
                        ContentType = asset.ContentType,
                        Url = asset.Url,
                        BrowserDownloadUrl = asset.BrowserDownloadUrl,
                    }).ToArray(),
                }));
            if (pageItems.Length < PageSize)
            {
                return releases
                    .OrderByDescending(release => release.PublishedAt)
                    .ToArray();
            }
        }

        throw new InvalidDataException(
            $"GitHub release pagination exceeds the {MaximumPages * PageSize} item limit.");
    }

    private async Task<GitHubReleasePageItem[]> GetPageAsync(
        int page,
        CancellationToken cancellationToken)
    {
        using var timeout = _requestTimeout == Timeout.InfiniteTimeSpan
            ? null
            : new CancellationTokenSource(_requestTimeout);
        using var linked = timeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked?.Token ?? cancellationToken;
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_releasesApiUrl}?per_page={PageSize}&page={page}");
        try
        {
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                token).ConfigureAwait(false);
            var responseUri = response.RequestMessage?.RequestUri ?? request.RequestUri;
            if (!HttpMediaUriValidator.HasSameOrigin(ApiOrigin, responseUri))
            {
                throw new InvalidDataException("GitHub release response changed to an untrusted origin.");
            }
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new InvalidDataException("GitHub release API redirects are not allowed.");
            }
            response.EnsureSuccessStatusCode();
            return await BoundedHttpContentReader.ReadJsonAsync<GitHubReleasePageItem[]>(
                       response.Content,
                       MaximumPageBytes,
                       JsonOptions,
                       token).ConfigureAwait(false)
                   ?? throw new InvalidDataException("GitHub returned an invalid release list.");
        }
        catch (OperationCanceledException exception)
            when (timeout?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"GitHub release request exceeded the {_requestTimeout} timeout.", exception);
        }
    }

    internal Task<GithubRelease[]> GetReleasesForTestsAsync(
        bool includePrereleases,
        CancellationToken cancellationToken = default)
        => GetReleasesAsync(includePrereleases, cancellationToken);

    private sealed record GitHubReleasePageItem(
        string? Name,
        bool Draft,
        bool Prerelease,
        [property: JsonPropertyName("published_at")] DateTime? PublishedAt,
        GitHubReleasePageAsset[]? Assets);

    private sealed record GitHubReleasePageAsset(
        string? Name,
        [property: JsonPropertyName("content_type")] string? ContentType,
        string? Url,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);
}
