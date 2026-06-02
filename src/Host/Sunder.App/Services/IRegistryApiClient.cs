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

    Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(
        string? query,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RegistryStackSummary>>([]);

    Task<RegistryStackDetails?> GetStackAsync(
        string stackId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<RegistryStackDetails?>(null);

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

    Task<RegistryCurrentUserResponse?> GetCurrentUserAsync(
        string bearerToken,
        CancellationToken cancellationToken = default)
        => Task.FromResult<RegistryCurrentUserResponse?>(null);

    Task<RegistryCliTokenResponse> ExchangeCliTokenAsync(
        string code,
        string codeVerifier,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new RegistryCliTokenResponse(false, null, null, null, ["Registry token exchange is not supported by this client."]));

    Task<RegistryResolveInstallPlanResponse> ResolvePackageChangesAsync(
        RegistryResolvePackageChangesRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new RegistryResolveInstallPlanResponse(
            false,
            [],
            [],
            ["Registry client does not support batch package change planning."],
            []));

    Task DownloadArtifactAsync(
        RegistryPackageArtifact artifact,
        string packageId,
        string version,
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task DownloadStackAsync(
        RegistryStackArtifact artifact,
        string stackId,
        string destinationPath,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Registry client does not support Stack downloads.");

    Task<RegistryPublishStackResponse> PublishStackAsync(
        string stackPath,
        string bearerToken,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new RegistryPublishStackResponse(false, null, null, [], ["Registry Stack publishing is not supported by this client."]));

    Task<RegistryStackManagementOperationResponse> DeleteStackAsync(
        string stackId,
        string bearerToken,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new RegistryStackManagementOperationResponse(false, null, ["Registry Stack deletion is not supported by this client."]));
}
