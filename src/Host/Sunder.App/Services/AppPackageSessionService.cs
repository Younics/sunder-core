using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

public sealed class AppPackageSessionService(
    IRuntimeApiClientFactory runtimeApiClientFactory,
    DeveloperLogService? developerLog = null) : IPackageInstalledSessionControl, IPackageDevelopmentSessionControl, IDisposable
{
    private const string DevelopmentUnavailableReason =
        "Development package loading is unavailable because the App cannot pass local paths to the Runtime. Start the Runtime with --dev-package instead.";

    private Func<IReadOnlyList<string>, CancellationToken, Task>? _applyPackageLifecycleChangesAsync;
    private Func<IReadOnlyList<ActivePackageDescriptor>, IReadOnlyList<PackageUiSnapshotDescriptor>, IReadOnlyList<string>, CancellationToken, Task>? _preflightPackageLifecycleChangesAsync;
    private bool _disposed;

    public PackageDevelopmentSessionAvailability Availability { get; } = new(false, DevelopmentUnavailableReason);

    public void Attach(
        Func<IReadOnlyList<string>, CancellationToken, Task> applyPackageLifecycleChangesAsync,
        Func<IReadOnlyList<ActivePackageDescriptor>, IReadOnlyList<PackageUiSnapshotDescriptor>, IReadOnlyList<string>, CancellationToken, Task> preflightPackageLifecycleChangesAsync)
    {
        _applyPackageLifecycleChangesAsync = applyPackageLifecycleChangesAsync;
        _preflightPackageLifecycleChangesAsync = preflightPackageLifecycleChangesAsync;
    }

    public void Detach(Func<IReadOnlyList<string>, CancellationToken, Task> applyPackageLifecycleChangesAsync)
    {
        if (Equals(_applyPackageLifecycleChangesAsync, applyPackageLifecycleChangesAsync))
        {
            _applyPackageLifecycleChangesAsync = null;
            _preflightPackageLifecycleChangesAsync = null;
        }
    }

    public async Task<InstalledPackageSessionStatus> LoadInstalledPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        cancellationToken.ThrowIfCancellationRequested();
        using var runtimeApiClient = CreateRuntimeApiClient();
        var result = await runtimeApiClient.LoadPackageSessionAsync(
            new Sunder.Runtime.Contracts.PackageSessionLoadRequest(PackageSourceKind.Installed, packageId),
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Errors.FirstOrDefault() ?? result.Message ?? "Installed package session load failed.");
        }

        await ApplyLifecycleChangesAsync(result.ImpactedPackageIds, cancellationToken).ConfigureAwait(false);
        return ToInstalledStatus(result.Status)
               ?? throw new InvalidOperationException("Runtime did not return installed package session status after loading the package.");
    }

    public async Task<bool> UnloadInstalledPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(packageId)) return false;

        using var runtimeApiClient = CreateRuntimeApiClient();
        var result = await runtimeApiClient.UnloadPackageSessionAsync(
            packageId,
            PackageSourceKind.Installed,
            cancellationToken).ConfigureAwait(false);
        if (!result.Success) return false;

        await ApplyLifecycleChangesAsync(result.ImpactedPackageIds, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<InstalledPackageSessionStatus?> GetInstalledPackageStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(packageId)) return null;

        using var runtimeApiClient = CreateRuntimeApiClient();
        return ToInstalledStatus(await runtimeApiClient.GetPackageSessionStatusAsync(packageId, cancellationToken).ConfigureAwait(false));
    }

    public Task<PackageDevelopmentSessionOperationResult> LoadDevelopmentPackageAsync(
        PackageDevelopmentSessionLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PackageDevelopmentSessionOperationResult(
            PackageDevelopmentSessionOperationOutcome.Unsupported,
            DevelopmentUnavailableReason));
    }

    public Task<PackageDevelopmentSessionOperationResult> UnloadDevelopmentPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PackageDevelopmentSessionOperationResult(
            PackageDevelopmentSessionOperationOutcome.Unsupported,
            DevelopmentUnavailableReason));
    }

    public Task<PackageDevelopmentSessionStatus?> GetDevelopmentPackageStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<PackageDevelopmentSessionStatus?>(null);
    }

    public void Dispose()
    {
        _ = developerLog;
        if (_disposed) return;
        _disposed = true;
    }

    private async Task ApplyLifecycleChangesAsync(
        IReadOnlyList<string> impactedPackageIds,
        CancellationToken cancellationToken)
    {
        if (impactedPackageIds.Count == 0) return;
        var applyPackageLifecycleChangesAsync = _applyPackageLifecycleChangesAsync
            ?? throw new InvalidOperationException("Installed package session control is not attached to the running shell yet.");
        await applyPackageLifecycleChangesAsync(impactedPackageIds, cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private IRuntimePackageSessionClient CreateRuntimeApiClient()
        => runtimeApiClientFactory.CreateClient<IRuntimePackageSessionClient>();

    private static InstalledPackageSessionStatus? ToInstalledStatus(Sunder.Runtime.Contracts.PackageSessionStatus? status)
        => status is null || status.ActiveSourceKind != PackageSourceKind.Installed && !status.OverridesInstalledPackage
            ? null
            : new InstalledPackageSessionStatus(
                status.PackageId,
                status.DisplayName,
                status.Version,
                status.ActiveSourceKind == PackageSourceKind.Installed && status.IsLoaded,
                status.ErrorMessage);
}
