using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed partial class RuntimeApiClient
{
    public Task<ContentUploadDescriptor> UploadPackageAsync(string packagePath, CancellationToken cancellationToken = default)
        => _management.UploadPackageAsync(packagePath, cancellationToken);

    public Task<ContentUploadDescriptor> UploadStackAsync(string stackPath, CancellationToken cancellationToken = default)
        => _management.UploadStackAsync(stackPath, cancellationToken);

    public Task<ContentUploadDescriptor> UploadStackMediaAsync(string mediaPath, string contentType, CancellationToken cancellationToken = default)
        => _management.UploadStackMediaAsync(mediaPath, contentType, cancellationToken);

    public Task DownloadContentAsync(ContentDownloadDescriptor download, string destinationPath, CancellationToken cancellationToken = default)
        => _management.DownloadContentAsync(download, destinationPath, cancellationToken);
}
