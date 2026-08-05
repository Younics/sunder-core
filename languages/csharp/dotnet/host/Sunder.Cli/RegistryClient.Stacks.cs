using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;

namespace Sunder.Cli;

internal sealed partial class RegistryClient
{
    private const long MaxStackDownloadBytes = 256L * 1024 * 1024;
    public Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(string? query, int skip, int take, CancellationToken token)
    {
        var path = $"{ApiRoot}/stacks?skip={skip}&take={Math.Clamp(take, 1, 100)}" + (string.IsNullOrWhiteSpace(query) ? string.Empty : $"&query={Uri.EscapeDataString(query.Trim())}");
        return GetRequiredAsync<IReadOnlyList<RegistryStackSummary>>(path, token);
    }

    public Task<RegistryStackDetails?> GetStackAsync(string stackId, CancellationToken token)
        => GetOrNullAsync<RegistryStackDetails>($"{ApiRoot}/stacks/{Uri.EscapeDataString(stackId)}", token);

    public Task<RegistryPublishStackResponse> PublishLocalStackAsync(string stackPath, CancellationToken token)
        => PostAsync<RegistryPublishLocalStackRequest, RegistryPublishStackResponse>(
            $"{ApiRoot}/dev/stacks/publish/local", new(stackPath), token, acceptErrorPayload: true);

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
            using var response = await _httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            RegistryTransportSecurity.RequireSameOrigin(RegistryApiUrl, response.RequestMessage?.RequestUri);
            await EnsureSuccessAsync(response, token).ConfigureAwait(false);
            if (artifact.Size is < 0 or > MaxStackDownloadBytes)
            {
                throw new InvalidDataException($"Downloaded Stack '{stackId}' exceeds the Stack download limit.");
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
