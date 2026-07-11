using System.Buffers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryPackageChangeOrchestrator(
    IHttpClientFactory httpClientFactory,
    RuntimeContentTransferStore transferStore,
    PackageSessionLifecycleService packageSessions,
    InstalledPackageLifecycleService installedPackages,
    ILogger<RegistryPackageChangeOrchestrator> logger)
{
    private const long MaxArtifactBytes = RuntimeContentTransferStore.MaxPackageUploadBytes;

    public async Task<RegistryResolveInstallPlanResponse> ResolveAsync(
        RuntimeRegistryPackageBatchRequest request,
        CancellationToken cancellationToken)
    {
        var origin = RegistryOrigin.Normalize(request.RegistryOrigin);
        var installed = await GetInstalledStateAsync(cancellationToken);
        return await ResolveCoreAsync(
            origin,
            new RegistryResolvePackageChangesRequest(
                request.Packages,
                installed,
                request.IncludePrerelease,
                request.AllowDowngrade,
                request.Reinstall),
            cancellationToken);
    }

    public Task<RuntimeRegistryPackageChangeResult> InstallAsync(
        RuntimeRegistryPackageRequest request,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            new RuntimeRegistryPackageBatchRequest(
                request.RegistryOrigin,
                [new RegistryPackageChangeRequest(request.PackageId, request.Version, request.Version is null ? request.Tag : null)],
                request.IncludePrerelease,
                request.AllowDowngrade,
                request.Reinstall),
            cancellationToken);

    public async Task<RuntimeRegistryPackageChangeResult> UpdateAsync(
        RuntimeRegistryUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var installed = await installedPackages.GetInstalledAsync(cancellationToken);
        var selected = string.IsNullOrWhiteSpace(request.PackageId)
            ? installed
            : installed.Where(package => string.Equals(package.PackageId, request.PackageId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Count == 0)
        {
            return Failed(
                string.IsNullOrWhiteSpace(request.PackageId) ? "No packages are installed." : $"Package '{request.PackageId}' is not installed.",
                RuntimeRegistryErrorCode.NotFound);
        }

        return await ExecuteAsync(
            new RuntimeRegistryPackageBatchRequest(
                request.RegistryOrigin,
                selected.Select(package => new RegistryPackageChangeRequest(package.PackageId, null, "latest")).ToArray(),
                request.IncludePrerelease),
            cancellationToken);
    }

    public async Task<RuntimeRegistryPackageChangeResult> ExecuteAsync(
        RuntimeRegistryPackageBatchRequest request,
        CancellationToken cancellationToken)
    {
        RegistryResolveInstallPlanResponse plan;
        Uri origin;
        try
        {
            origin = RegistryOrigin.Normalize(request.RegistryOrigin);
            plan = await ResolveAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Registry package plan failed: {ErrorType}", ex.GetType().Name);
            return Failed(ex.Message, RuntimeRegistryErrorCode.RegistryUnavailable);
        }

        if (!plan.Success)
        {
            var errors = plan.Errors.Concat(plan.Conflicts.Select(conflict => conflict.Message)).DefaultIfEmpty("Package plan resolution failed.").ToArray();
            return new RuntimeRegistryPackageChangeResult(false, RuntimeRegistryErrorCode.Conflict, errors[0], false, false, plan.Warnings, errors, [], plan.Items);
        }

        if (plan.Items.Count == 0)
        {
            return new RuntimeRegistryPackageChangeResult(true, RuntimeRegistryErrorCode.None, "No package changes required.", true, false, plan.Warnings, [], [], []);
        }

        var uploadIds = new List<string>();
        string? stageId = null;
        try
        {
            var mutations = new List<PackageStoreMutationRequest>(plan.Items.Count);
            foreach (var item in plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateArtifactUri(origin, item.Artifact.DownloadUrl);
                using var response = await SendAsync(
                    new HttpRequestMessage(HttpMethod.Get, new Uri(origin, item.Artifact.DownloadUrl)),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaxArtifactBytes)
                {
                    return Failed($"Package '{item.PackageId}' exceeds the {MaxArtifactBytes} byte download limit.", RuntimeRegistryErrorCode.DownloadTooLarge, plan);
                }

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var verified = new HashVerifyingReadStream(source, MaxArtifactBytes);
                var upload = await transferStore.CreateUploadAsync(
                    RuntimeUploadKind.Package,
                    verified,
                    response.Content.Headers.ContentLength,
                    item.Artifact.Sha256,
                    $"{item.PackageId}.{item.Version}.sunderpkg",
                    "application/vnd.sunder.package",
                    packageSessions.Generation,
                    cancellationToken);
                if (item.Artifact.Size > 0 && upload.Length != item.Artifact.Size)
                {
                    return Failed($"Package '{item.PackageId}' size verification failed.", RuntimeRegistryErrorCode.ArtifactVerificationFailed, plan);
                }

                uploadIds.Add(upload.UploadId);
                mutations.Add(new PackageStoreMutationRequest(
                    item.CurrentVersion is null ? PackageStoreMutationKind.Install : PackageStoreMutationKind.Upgrade,
                    item.CurrentVersion is null ? null : item.PackageId,
                    upload.UploadId,
                    request.AllowDowngrade,
                    request.Reinstall));
            }

            var stage = await installedPackages.StageAsync(new PackageStoreStageRequest(mutations), cancellationToken);
            stageId = stage.StageId;
            if (!stage.Success || stageId is null)
            {
                var errors = stage.Errors.DefaultIfEmpty(stage.OperationResult.Message ?? "Package transaction staging failed.").ToArray();
                return new RuntimeRegistryPackageChangeResult(false, RuntimeRegistryErrorCode.Conflict, errors[0], false, false, plan.Warnings.Concat(stage.Warnings).ToArray(), errors, stage.ImpactedPackageIds, plan.Items);
            }

            var commit = await installedPackages.CommitStageAsync(stageId, cancellationToken);
            stageId = null;
            var commitErrors = commit.Errors.DefaultIfEmpty(commit.Message ?? "Package transaction commit failed.").ToArray();
            return new RuntimeRegistryPackageChangeResult(
                commit.Success,
                commit.Success ? RuntimeRegistryErrorCode.None : RuntimeRegistryErrorCode.InternalError,
                commit.Message ?? (commit.Success ? $"Applied {plan.Items.Count} package change(s)." : commitErrors[0]),
                commit.RuntimeSessionApplied,
                commit.RequiresAppRestart,
                plan.Warnings.Concat(commit.Warnings).ToArray(),
                commit.Success ? [] : commitErrors,
                commit.ImpactedPackageIds,
                plan.Items);
        }
        catch (OperationCanceledException)
        {
            if (stageId is not null)
            {
                await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            }
            throw;
        }
        catch (InvalidDataException ex)
        {
            if (stageId is not null)
            {
                await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            }
            return Failed(ex.Message, RuntimeRegistryErrorCode.ArtifactVerificationFailed, plan);
        }
        catch (Exception ex)
        {
            if (stageId is not null)
            {
                await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            }
            logger.LogWarning("Registry package transaction failed: {ErrorType}", ex.GetType().Name);
            return Failed(ex.Message, RuntimeRegistryErrorCode.RegistryUnavailable, plan);
        }
        finally
        {
            foreach (var uploadId in uploadIds)
            {
                transferStore.DiscardUpload(uploadId);
            }
        }
    }

    private async Task<RegistryResolveInstallPlanResponse> ResolveCoreAsync(
        Uri origin,
        RegistryResolvePackageChangesRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "api/v1/packages/resolve-package-changes"))
            {
                Content = JsonContent.Create(request),
            },
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        var result = await TryReadAsync<RegistryResolveInstallPlanResponse>(response, cancellationToken);
        return result ?? new RegistryResolveInstallPlanResponse(false, [], [], [response.ReasonPhrase ?? "Package plan resolution failed."], []);
    }

    private async Task<IReadOnlyList<RegistryInstalledPackageState>> GetInstalledStateAsync(CancellationToken cancellationToken)
        => (await installedPackages.GetInstalledAsync(cancellationToken))
            .Select(package => new RegistryInstalledPackageState(
                package.PackageId,
                package.Version,
                package.DependsOn.Select(dependency => new RegistryPackageDependency(dependency.PackageId, dependency.VersionRange)).ToArray()))
            .ToArray();

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("registry");
        return await client.SendAsync(request, completion, cancellationToken);
    }

    private static async Task<T?> TryReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            return default;
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

    private static RuntimeRegistryPackageChangeResult Failed(
        string message,
        RuntimeRegistryErrorCode errorCode,
        RegistryResolveInstallPlanResponse? plan = null)
        => new(false, errorCode, message, false, false, plan?.Warnings ?? [], [message], [], plan?.Items ?? []);

    private sealed class HashVerifyingReadStream(Stream inner, long maxLength) : Stream
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
            if (_length > maxLength)
            {
                throw new InvalidDataException($"Registry artifact exceeds the {maxLength} byte download limit.");
            }
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
