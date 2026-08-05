using Sunder.Registry.Contracts;

namespace Sunder.App.Services;

public interface IRegistryClient : IDisposable
{
    Uri RegistryUrl { get; }
}

public interface IRegistryPackageBrowseClient : IRegistryClient
{
    Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, RegistrySearchSort sort = RegistrySearchSort.Downloads, CancellationToken cancellationToken = default);
    Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default);
    Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken cancellationToken = default);
}

public interface IRegistryStackBrowseClient : IRegistryClient
{
    Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(string? query, int skip, int take, RegistrySearchSort sort = RegistrySearchSort.Downloads, CancellationToken cancellationToken = default);
    Task<RegistryStackDetails?> GetStackAsync(string stackId, CancellationToken cancellationToken = default);
    Task DownloadStackAsync(RegistryStackArtifact artifact, string stackId, string destinationPath, CancellationToken cancellationToken = default);
}

public interface IRegistryStackDetailClient : IRegistryPackageBrowseClient, IRegistryStackBrowseClient;

public interface IRegistryApiClient : IRegistryStackDetailClient;
