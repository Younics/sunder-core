using System.Security.Cryptography;
using Sunder.Registry.Contracts;

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

    public async Task DownloadStackAsync(RegistryStackArtifact artifact, string stackId, string destinationPath, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using var response = await _httpClient.GetAsync(CreateUri(artifact.DownloadUrl), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, token).ConfigureAwait(false);
            if (artifact.Size is < 0 or > MaxStackDownloadBytes)
            {
                throw new InvalidDataException($"Downloaded Stack '{stackId}' exceeds the Stack download limit.");
            }
            await using var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CliHttpContentReader.CopyToAsync(
                response.Content,
                destination,
                artifact.Size ?? MaxStackDownloadBytes,
                token).ConfigureAwait(false);
            await destination.DisposeAsync().ConfigureAwait(false);

            var length = new FileInfo(temporaryPath).Length;
            if (artifact.Size is { } expectedSize && length != expectedSize) throw new InvalidDataException($"Downloaded Stack '{stackId}' size mismatch.");
            if (!string.IsNullOrWhiteSpace(artifact.Sha256))
            {
                await using var stream = File.OpenRead(temporaryPath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
                if (!string.Equals(hash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Downloaded Stack '{stackId}' SHA-256 mismatch.");
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            CliFileCleanup.TryDelete(temporaryPath);
        }
    }
}
