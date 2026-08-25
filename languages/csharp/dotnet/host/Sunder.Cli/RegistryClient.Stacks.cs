using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;

namespace Sunder.Cli;

internal sealed partial class RegistryClient
{
    private const long MaxStackDownloadBytes = 256L * 1024 * 1024;
    public Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(string? query, int skip, int take, CancellationToken token)
    {
        var path = $"{RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.Stacks)}?skip={skip}&take={Math.Clamp(take, 1, 100)}" + (string.IsNullOrWhiteSpace(query) ? string.Empty : $"&query={Uri.EscapeDataString(query.Trim())}");
        return GetRequiredAsync<IReadOnlyList<RegistryStackSummary>>(path, token);
    }

    public Task<RegistryStackDetails?> GetStackAsync(string stackId, CancellationToken token)
        => GetOrNullAsync<RegistryStackDetails>(
            RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.Stack(stackId)),
            token);

    public Task<RegistryPublishStackResponse> PublishLocalStackAsync(string stackPath, CancellationToken token)
        => PostAsync<RegistryPublishLocalStackRequest, RegistryPublishStackResponse>(
            RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.DevelopmentStackPublishLocal),
            new(stackPath),
            token,
            acceptErrorPayload: true);

    public async Task DownloadStackAsync(RegistryStackArtifact artifact, string stackId, string destinationPath, bool force, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        if (!force && File.Exists(fullPath))
        {
            throw new CliConflictException($"Output file '{fullPath}' already exists. Use --force to replace it.");
        }

        try
        {
            var downloadUri = CreateUri(artifact.DownloadUrl);
            RegistryTransportSecurity.RequireSameOrigin(RegistryApiUrl, downloadUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            using var response = await SendAsync(request, token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, token).ConfigureAwait(false);
            if (artifact.Size is < 0 or > MaxStackDownloadBytes)
            {
                throw new InvalidDataException($"Downloaded Stack '{stackId}' exceeds the Stack download limit.");
            }
            if (response.Content.Headers.ContentLength is > MaxStackDownloadBytes
                || response.Content.Headers.ContentLength is { } contentLength
                && artifact.Size is { } expectedSize
                && contentLength != expectedSize)
            {
                throw new InvalidDataException($"Downloaded Stack '{stackId}' has an invalid content length.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await VerifiedFileTransfer.PublishAsync(
                source,
                fullPath,
                MaxStackDownloadBytes,
                artifact.Size,
                artifact.Sha256,
                $"Downloaded Stack '{stackId}'",
                token,
                overwrite: force).ConfigureAwait(false);
        }
        catch (IOException exception) when (!force && File.Exists(fullPath))
        {
            throw new CliConflictException($"Output file '{fullPath}' already exists. Use --force to replace it.", exception);
        }
    }

}
