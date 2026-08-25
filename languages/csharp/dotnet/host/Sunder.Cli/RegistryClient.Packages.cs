using Sunder.Registry.Contracts;

namespace Sunder.Cli;

internal sealed partial class RegistryClient
{
    public Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, CancellationToken token)
    {
        var path = $"{RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.Packages)}?skip={skip}&take={Math.Clamp(take, 1, 100)}" + (string.IsNullOrWhiteSpace(query) ? string.Empty : $"&query={Uri.EscapeDataString(query.Trim())}");
        return GetRequiredAsync<IReadOnlyList<RegistryPackageSummary>>(path, token);
    }

    public Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken token)
        => GetOrNullAsync<RegistryPackageDetails>(
            RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.Package(packageId)),
            token);

    public Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken token)
        => GetOrNullAsync<RegistryPackageVersionDetails>(
            RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.PackageVersion(packageId, version)),
            token);

    public Task<RegistryPackageDistTagsResponse?> GetDistTagsAsync(string packageId, CancellationToken token)
        => GetOrNullAsync<RegistryPackageDistTagsResponse>(
            RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.PackageDistTags(packageId)),
            token);

    public Task<RegistryPublishPackageResponse> PublishLocalPackageAsync(string packagePath, bool setLatest, CancellationToken token)
        => PostAsync<RegistryPublishLocalPackageRequest, RegistryPublishPackageResponse>(
            RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.DevelopmentPackagePublishLocal),
            new(packagePath, setLatest),
            token,
            acceptErrorPayload: true);
}
