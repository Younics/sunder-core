using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryPackageArtifactDownloader(
    RegistryHttpClient registryClient,
    RuntimeContentTransferStore transfers,
    PackageSessionLifecycleService packageSessions,
    RuntimeTransportPolicyOptions policy,
    TimeProvider timeProvider)
{
    private const long MaxArtifactBytes = RuntimeContentTransferStore.MaxPackageUploadBytes;

    public async Task<ContentUploadDescriptor> DownloadAsync(
        Uri origin,
        RegistryPackageInstallPlanItem item,
        CancellationToken cancellationToken)
    {
        ValidateArtifactUri(origin, item.Artifact.DownloadUrl);
        using var timeout = new CancellationTokenSource(policy.RegistryRequestTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var response = await registryClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, new Uri(origin, item.Artifact.DownloadUrl)),
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token);
            await registryClient.EnsureSuccessAsync(response, linked.Token);
            if (response.Content.Headers.ContentLength is > MaxArtifactBytes)
            {
                throw new RegistryArtifactTooLargeException($"Package '{item.PackageId}' exceeds the {MaxArtifactBytes} byte download limit.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(linked.Token);
            await using var bounded = new BoundedReadStream(source, MaxArtifactBytes);
            var upload = await transfers.CreateUploadAsync(
                RuntimeUploadKind.Package,
                bounded,
                response.Content.Headers.ContentLength,
                item.Artifact.Sha256,
                $"{item.PackageId}.{item.Version}.sunderpkg",
                "application/vnd.sunder.package",
                packageSessions.Generation,
                linked.Token);
            if (item.Artifact.Size > 0 && upload.Length != item.Artifact.Size)
            {
                transfers.DiscardUpload(upload.UploadId);
                throw new InvalidDataException($"Package '{item.PackageId}' size verification failed.");
            }
            return upload;
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Registry artifact transfer exceeded the {policy.RegistryRequestTimeout} timeout.", exception);
        }
    }

    private static void ValidateArtifactUri(Uri origin, string downloadUrl)
    {
        var artifact = new Uri(origin, downloadUrl);
        if (!string.Equals(artifact.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(artifact.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
            || artifact.Port != origin.Port)
        {
            throw new InvalidDataException("Registry artifact URL must remain on the trusted Registry origin.");
        }
    }

    private sealed class BoundedReadStream(Stream inner, long maxLength) : Stream
    {
        private long _length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            _length += read;
            if (_length > maxLength) throw new RegistryArtifactTooLargeException($"Registry artifact exceeds the {maxLength} byte download limit.");
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}

internal sealed class RegistryArtifactTooLargeException(string message) : Exception(message);
