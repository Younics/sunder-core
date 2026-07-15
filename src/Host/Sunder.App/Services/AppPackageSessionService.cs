using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

public sealed class AppPackageSessionService : IPackageDevelopmentSessionControl
{
    private const string DevelopmentUnavailableReason =
        "Development package loading is unavailable because the App cannot pass local paths to the Runtime. Start the Runtime with --dev-package instead.";

    public PackageDevelopmentSessionAvailability Availability { get; } = new(false, DevelopmentUnavailableReason);

    public Task<PackageDevelopmentSessionOperationResult> LoadDevelopmentPackageAsync(
        PackageDevelopmentSessionLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Unsupported());
    }

    public Task<PackageDevelopmentSessionOperationResult> UnloadDevelopmentPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Unsupported());
    }

    public Task<PackageDevelopmentSessionStatus?> GetDevelopmentPackageStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<PackageDevelopmentSessionStatus?>(null);
    }

    private static PackageDevelopmentSessionOperationResult Unsupported()
        => new(PackageDevelopmentSessionOperationOutcome.Unsupported, DevelopmentUnavailableReason);
}
