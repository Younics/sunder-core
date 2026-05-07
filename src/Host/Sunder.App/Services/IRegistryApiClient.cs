using Sunder.Registry.Shared;

namespace Sunder.App.Services;

public interface IRegistryApiClient : IDisposable
{
    Uri RegistryUrl { get; }

    Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(
        string? query,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    Task<RegistryPackageDetails?> GetPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    Task<RegistryPackageVersionDetails?> GetVersionAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken = default);

    Task<RegistryResolveUpdatesResponse> ResolveUpdatesAsync(
        RegistryResolveUpdatesRequest request,
        CancellationToken cancellationToken = default);

    Task<RegistryResolveInstallPlanResponse> ResolveInstallPlanAsync(
        RegistryResolveInstallPlanRequest request,
        CancellationToken cancellationToken = default);

    Task DownloadArtifactAsync(
        RegistryPackageArtifact artifact,
        string packageId,
        string version,
        string destinationPath,
        CancellationToken cancellationToken = default);
}
