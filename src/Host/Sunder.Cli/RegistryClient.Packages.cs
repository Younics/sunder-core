using Sunder.Registry.Contracts;

namespace Sunder.Cli;

internal sealed partial class RegistryClient
{
    public Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, CancellationToken token)
    {
        var path = $"{ApiRoot}/packages?skip={skip}&take={Math.Clamp(take, 1, 100)}" + (string.IsNullOrWhiteSpace(query) ? string.Empty : $"&query={Uri.EscapeDataString(query.Trim())}");
        return GetRequiredAsync<IReadOnlyList<RegistryPackageSummary>>(path, token);
    }

    public Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken token)
        => GetOrNullAsync<RegistryPackageDetails>($"{ApiRoot}/packages/{Uri.EscapeDataString(packageId)}", token);

    public Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken token)
        => GetOrNullAsync<RegistryPackageVersionDetails>($"{ApiRoot}/packages/{Uri.EscapeDataString(packageId)}/versions/{Uri.EscapeDataString(version)}", token);

    public Task<RegistryPackageDistTagsResponse?> GetDistTagsAsync(string packageId, CancellationToken token)
        => GetOrNullAsync<RegistryPackageDistTagsResponse>($"{ApiRoot}/packages/{Uri.EscapeDataString(packageId)}/dist-tags", token);

    public Task<RegistryPublishPackageResponse> PublishLocalPackageAsync(string packagePath, bool setLatest, CancellationToken token)
        => PostAsync<RegistryPublishLocalPackageRequest, RegistryPublishPackageResponse>(
            $"{ApiRoot}/dev/packages/publish/local", new(packagePath, setLatest), token, acceptErrorPayload: true);
}
