using Sunder.Registry.Contracts;

namespace Sunder.App.Services;

public interface IRegistryApiClient : IDisposable
{
    Uri RegistryUrl { get; }
    Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, RegistrySearchSort sort = RegistrySearchSort.Downloads, CancellationToken cancellationToken = default);
    Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(string? query, int skip, int take, RegistrySearchSort sort = RegistrySearchSort.Downloads, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RegistryStackSummary>>([]);
    Task<RegistryStackDetails?> GetStackAsync(string stackId, CancellationToken cancellationToken = default)
        => Task.FromResult<RegistryStackDetails?>(null);
    Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken cancellationToken = default);
    Task DownloadStackAsync(RegistryStackArtifact artifact, string stackId, string destinationPath, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
