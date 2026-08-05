using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    public async Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken token = default)
        => await GetRequiredAsync<IReadOnlyList<InstalledPackageDescriptor>>("packages/installed", token).ConfigureAwait(false);

    public async Task<PackageOperationResult> ApplyLocalPackageAsync(
        string packagePath,
        string packageId,
        bool allowDowngrade,
        bool reinstall,
        CancellationToken token = default)
    {
        var installed = await GetInstalledPackagesAsync(token).ConfigureAwait(false);
        var kind = installed.Any(item => string.Equals(item.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            ? PackageStoreMutationKind.Upgrade
            : PackageStoreMutationKind.Install;
        var upload = await UploadAsync(packagePath, "uploads/packages", "application/vnd.sunder.package", token).ConfigureAwait(false);
        using var stageResponse = await _httpClient.PostAsJsonAsync(
            CreateUri("packages/store/stage"),
            new PackageStoreStageRequest([new PackageStoreMutationRequest(kind, kind == PackageStoreMutationKind.Upgrade ? packageId : null, upload.UploadId, allowDowngrade, reinstall)]),
            token).ConfigureAwait(false);
        var stage = await _responses.ReadRequiredJsonAsync<PackageStoreStageResult>(stageResponse, token).ConfigureAwait(false);
        if (!stage.Success || stage.StageId is null)
        {
            return stage.OperationResult;
        }

        return await CommitPackageStageAsync(stage.StageId, stage.OperationResult, token).ConfigureAwait(false);
    }

    private async Task<ContentUploadDescriptor> UploadAsync(string filePath, string endpoint, string contentType, CancellationToken token)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
        stream.Position = 0;
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Headers.ContentLength = stream.Length;
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = Path.GetFileName(filePath) };
        content.Headers.Add("X-Content-SHA256", hash);
        using var response = await _httpClient.PostAsync(CreateUri(endpoint), content, token).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<ContentUploadDescriptor>(response, token).ConfigureAwait(false);
    }

}
