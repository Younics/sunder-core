using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.PackageSessionsV1)]
public interface IPackageSessionService
{
    Task<PackageSessionStatus> LoadPackageAsync(
        PackageSessionLoadRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> UnloadPackageAsync(
        string packageId,
        PackageSessionSourceKind sourceKind,
        CancellationToken cancellationToken = default);

    Task<PackageSessionStatus?> GetPackageStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default);
}

[SunderSdkCapability(SunderSdkCapabilities.PackageSessionsV1)]
public sealed record PackageSessionLoadRequest(
    PackageSessionSourceKind SourceKind,
    string Source,
    bool Watch = false);

[SunderSdkCapability(SunderSdkCapabilities.PackageSessionsV1)]
public sealed record PackageSessionStatus(
    string PackageId,
    string? DisplayName,
    string? Version,
    PackageSessionSourceKind ActiveSourceKind,
    bool IsLoaded,
    bool WatchEnabled,
    bool OverridesInstalledPackage,
    string? ErrorMessage);

[SunderSdkCapability(SunderSdkCapabilities.PackageSessionsV1)]
public enum PackageSessionSourceKind
{
    Installed = 0,
    Dev = 1,
}

[SunderSdkCapability(SunderSdkCapabilities.PackageSessionsV1)]
public sealed class NullPackageSessionService : IPackageSessionService
{
    public static NullPackageSessionService Instance { get; } = new();

    private NullPackageSessionService()
    {
    }

    public Task<PackageSessionStatus> LoadPackageAsync(
        PackageSessionLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<PackageSessionStatus>(new NotSupportedException("Package session loading is not available in this host context."));
    }

    public Task<bool> UnloadPackageAsync(
        string packageId,
        PackageSessionSourceKind sourceKind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public Task<PackageSessionStatus?> GetPackageStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<PackageSessionStatus?>(null);
    }
}
