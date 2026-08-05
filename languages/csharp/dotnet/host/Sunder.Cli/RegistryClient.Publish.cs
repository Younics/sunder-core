using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;

namespace Sunder.Cli;

internal sealed partial class RegistryClient
{
    private const long MaxPackagePublishBytes = 512L * 1024 * 1024;
    private const long MaxStackPublishBytes = 256L * 1024 * 1024;

    public async Task<RegistryPublishPackageResponse> PublishPackageAsync(
        string packagePath,
        bool setLatest,
        RegistryPublishCredential credential,
        CancellationToken token)
    {
        var expectedPackageId = await ReadExpectedResourceIdAsync<SunderPackageManifest>(
            packagePath,
            SunderPackageFormat.ManifestPath,
            MaxPackagePublishBytes,
            static manifest => manifest.Id,
            token).ConfigureAwait(false);
        return await PublishArchiveAsync<RegistryPublishPackageResponse>(
            packagePath,
            "package",
            "application/vnd.sunder.package",
            $"{ApiRoot}/packages/publish",
            MaxPackagePublishBytes,
            credential,
            expectedPackageId,
            setLatest,
            content => content.Add(new StringContent(setLatest ? "true" : "false"), "setLatest"),
            token).ConfigureAwait(false);
    }

    public async Task<RegistryPublishStackResponse> PublishStackAsync(
        string stackPath,
        RegistryPublishCredential credential,
        CancellationToken token)
    {
        var expectedStackId = await ReadExpectedResourceIdAsync<SunderStackManifest>(
            stackPath,
            SunderStackFormat.ManifestPath,
            MaxStackPublishBytes,
            static manifest => manifest.StackId,
            token).ConfigureAwait(false);
        return await PublishArchiveAsync<RegistryPublishStackResponse>(
            stackPath,
            "stack",
            "application/vnd.sunder.stack",
            $"{ApiRoot}/stacks/publish",
            MaxStackPublishBytes,
            credential,
            expectedStackId,
            setLatest: null,
            configure: null,
            token).ConfigureAwait(false);
    }

    private async Task<TResponse> PublishArchiveAsync<TResponse>(
        string archivePath,
        string fieldName,
        string contentType,
        string requestPath,
        long maxBytes,
        RegistryPublishCredential credential,
        string expectedResourceId,
        bool? setLatest,
        Action<MultipartFormDataContent>? configure,
        CancellationToken token)
    {
        var fullPath = Path.GetFullPath(archivePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists) throw new FileNotFoundException("The publication archive was not found.", fullPath);
        if (file.Length <= 0 || file.Length > maxBytes)
            throw new InvalidDataException($"The publication archive must be between 1 and {maxBytes} bytes.");

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var multipart = new MultipartFormDataContent();
        using var archive = new StreamContent(stream, 128 * 1024);
        archive.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        multipart.Add(archive, fieldName, file.Name);
        configure?.Invoke(multipart);

        using var request = new HttpRequestMessage(HttpMethod.Post, CreateUri(requestPath))
        {
            Content = multipart,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Value);
        request.Headers.Add(RegistryPublishRequestHeaders.ExpectedResourceId, expectedResourceId);
        if (setLatest is not null)
        {
            request.Headers.Add(RegistryPublishRequestHeaders.SetLatest, setLatest.Value ? "true" : "false");
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            token).ConfigureAwait(false);
        RegistryTransportSecurity.RequireSameOrigin(RegistryApiUrl, response.RequestMessage?.RequestUri);
        return await ReadRequiredAsync<TResponse>(response, token, acceptErrorPayload: true)
            .ConfigureAwait(false);
    }

    private static async Task<string> ReadExpectedResourceIdAsync<TManifest>(
        string archivePath,
        string manifestPath,
        long maxArchiveBytes,
        Func<TManifest, string?> selectResourceId,
        CancellationToken token)
    {
        const long maxManifestBytes = 1024 * 1024;
        var fullPath = Path.GetFullPath(archivePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The publication archive was not found.", fullPath);
        }

        if (file.Length <= 0 || file.Length > maxArchiveBytes)
        {
            throw new InvalidDataException($"The publication archive must be between 1 and {maxArchiveBytes} bytes.");
        }

        using var archive = ZipFile.OpenRead(fullPath);
        var entries = archive.Entries
            .Where(entry => string.Equals(entry.FullName, manifestPath, StringComparison.Ordinal))
            .ToArray();
        if (entries.Length != 1 || entries[0].Length is <= 0 or > maxManifestBytes)
        {
            throw new InvalidDataException($"The publication archive must contain one bounded '{manifestPath}' manifest.");
        }

        TManifest? manifest;
        try
        {
            await using var stream = entries[0].Open();
            manifest = await JsonSerializer.DeserializeAsync<TManifest>(stream, cancellationToken: token)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The publication archive manifest '{manifestPath}' is invalid.", ex);
        }

        var resourceId = manifest is null ? null : selectResourceId(manifest)?.Trim();
        return !string.IsNullOrWhiteSpace(resourceId)
            ? resourceId
            : throw new InvalidDataException($"The publication archive manifest '{manifestPath}' has no resource id.");
    }
}
