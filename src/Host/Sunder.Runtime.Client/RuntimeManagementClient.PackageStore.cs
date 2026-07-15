using System.Net.Http.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    public Task<ContentUploadDescriptor> UploadPackageAsync(string filePath, CancellationToken token = default)
        => UploadAsync(filePath, "uploads/packages", "application/vnd.sunder.package", token);

    public Task<ContentUploadDescriptor> UploadStackAsync(string filePath, CancellationToken token = default)
        => UploadAsync(filePath, "uploads/stacks", "application/vnd.sunder.stack", token);

    public Task<ContentUploadDescriptor> UploadStackMediaAsync(
        string filePath,
        string contentType,
        CancellationToken token = default)
        => UploadAsync(filePath, "uploads/stack-media", contentType, token);

    public async Task<PackageStoreStageResult> StagePackageStoreChangesAsync(
        PackageStoreStageRequest request,
        CancellationToken token = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(CreateUri("packages/store/stage"), request, token).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<PackageStoreStageResult>(response, token).ConfigureAwait(false);
    }

    public Task<PackageOperationResult> CommitPackageStoreStageAsync(
        string stageId,
        PackageOperationResult stagedResult,
        CancellationToken token = default)
        => CommitPackageStageAsync(stageId, stagedResult, token);

    public Task<PackageOperationResult> CommitPackageStoreStageAsync(string stageId, CancellationToken token = default)
        => CommitPackageStageAsync(
            stageId,
            new PackageOperationResult(true, "Package store stage committed.", false, false, [], []),
            token);

    public async Task DiscardPackageStoreStageAsync(string stageId, CancellationToken token = default)
    {
        using var response = await _httpClient.DeleteAsync(
            CreateUri($"packages/store/stage/{Uri.EscapeDataString(stageId)}"),
            token).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            await _responses.EnsureSuccessAsync(response, token).ConfigureAwait(false);
        }
    }
}
