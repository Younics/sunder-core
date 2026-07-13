using System.Security.Cryptography;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed partial class RuntimeApiClient
{
    public Task<ContentUploadDescriptor> UploadPackageAsync(string packagePath, CancellationToken cancellationToken = default)
        => UploadFileAsync(packagePath, "uploads/packages", "application/vnd.sunder.package", cancellationToken);

    public Task<ContentUploadDescriptor> UploadStackAsync(string stackPath, CancellationToken cancellationToken = default)
        => UploadFileAsync(stackPath, "uploads/stacks", "application/vnd.sunder.stack", cancellationToken);

    public Task<ContentUploadDescriptor> UploadStackMediaAsync(string mediaPath, string contentType, CancellationToken cancellationToken = default)
        => UploadFileAsync(mediaPath, "uploads/stack-media", contentType, cancellationToken);

    public async Task DownloadContentAsync(ContentDownloadDescriptor download, string destinationPath, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            CreateRequestUri(download.DownloadUri),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await _responses.EnsureSuccessAsync(response, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long length = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (length > download.Length)
            {
                throw new InvalidDataException("Runtime download exceeded its declared length.");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (length != download.Length || !string.Equals(actualHash, download.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            destination.Close();
            File.Delete(destinationPath);
            throw new InvalidDataException("Runtime download hash or length verification failed.");
        }
    }
}
