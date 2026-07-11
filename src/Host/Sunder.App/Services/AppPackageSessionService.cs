using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using SdkPackageSessionLoadRequest = Sunder.Sdk.Abstractions.PackageSessionLoadRequest;
using SdkPackageSessionSourceKind = Sunder.Sdk.Abstractions.PackageSessionSourceKind;
using SdkPackageSessionStatus = Sunder.Sdk.Abstractions.PackageSessionStatus;

namespace Sunder.App.Services;

public sealed class AppPackageSessionService(
    IRuntimeApiClientFactory runtimeApiClientFactory,
    DeveloperLogService? developerLog = null) : IPackageSessionService, IDisposable
{
    private Func<IReadOnlyList<string>, CancellationToken, Task>? _applyPackageLifecycleChangesAsync;
    private Func<IReadOnlyList<ActivePackageDescriptor>, IReadOnlyList<PackageUiSnapshotDescriptor>, IReadOnlyList<string>, CancellationToken, Task>? _preflightPackageLifecycleChangesAsync;
    private bool _disposed;

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

    public async Task<SdkPackageSessionStatus> LoadPackageAsync(
        SdkPackageSessionLoadRequest request,
        CancellationToken cancellationToken = default)
        => await LoadPackageCoreAsync(request, updateWatch: true, cancellationToken).ConfigureAwait(false);

    public async Task<bool> UnloadPackageAsync(
        string packageId,
        SdkPackageSessionSourceKind sourceKind,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        using var runtimeApiClient = runtimeApiClientFactory.CreateClient();
        var result = await runtimeApiClient.UnloadPackageSessionAsync(
            packageId,
            ToProtocolSourceKind(sourceKind),
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return false;
        }

        await ApplyLifecycleChangesAsync(result.ImpactedPackageIds, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<SdkPackageSessionStatus?> GetPackageStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        using var runtimeApiClient = runtimeApiClientFactory.CreateClient();
        return ToSdkStatus(await runtimeApiClient.GetPackageSessionStatusAsync(packageId, cancellationToken).ConfigureAwait(false));
    }

    public void Dispose()
    {
        _ = developerLog;
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }

    private async Task<SdkPackageSessionStatus> LoadPackageCoreAsync(
        SdkPackageSessionLoadRequest request,
        bool updateWatch,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        using var runtimeApiClient = runtimeApiClientFactory.CreateClient();
        var status = request.SourceKind == SdkPackageSessionSourceKind.Dev
            ? await LoadDevPackageLifecycleAsync(runtimeApiClient, request, cancellationToken).ConfigureAwait(false)
            : await LoadPackageSessionAsync(runtimeApiClient, request, cancellationToken).ConfigureAwait(false);

        return status;
    }

    private async Task<SdkPackageSessionStatus> LoadPackageSessionAsync(
        IRuntimeApiClient runtimeApiClient,
        SdkPackageSessionLoadRequest request,
        CancellationToken cancellationToken)
    {
        var result = await runtimeApiClient.LoadPackageSessionAsync(
            new Sunder.Runtime.Contracts.PackageSessionLoadRequest(
                ToProtocolSourceKind(request.SourceKind),
                request.Source),
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            var message = result.Errors.FirstOrDefault() ?? result.Message ?? "Package session load failed.";
            throw new InvalidOperationException(message);
        }

        await ApplyLifecycleChangesAsync(result.ImpactedPackageIds, cancellationToken).ConfigureAwait(false);
        return ToSdkStatus(result.Status)
               ?? throw new InvalidOperationException("Runtime did not return package session status after loading the package.");
    }

    private Task<SdkPackageSessionStatus> LoadDevPackageLifecycleAsync(
        IRuntimeApiClient runtimeApiClient,
        SdkPackageSessionLoadRequest request,
        CancellationToken cancellationToken)
        => Task.FromException<SdkPackageSessionStatus>(new InvalidOperationException(
            "Dev package folders must be supplied to Runtime startup with --dev-package; remote path loading is not supported."));

    private async Task ApplyLifecycleChangesAsync(
        IReadOnlyList<string> impactedPackageIds,
        CancellationToken cancellationToken)
    {
        if (impactedPackageIds.Count == 0)
        {
            return;
        }

        var applyPackageLifecycleChangesAsync = _applyPackageLifecycleChangesAsync
            ?? throw new InvalidOperationException("Package session service is not attached to the running shell yet.");
        await applyPackageLifecycleChangesAsync(impactedPackageIds, cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private static PackageSourceKind ToProtocolSourceKind(SdkPackageSessionSourceKind sourceKind)
        => sourceKind switch
        {
            SdkPackageSessionSourceKind.Installed => PackageSourceKind.Installed,
            SdkPackageSessionSourceKind.Dev => PackageSourceKind.Dev,
            _ => throw new ArgumentOutOfRangeException(nameof(sourceKind), sourceKind, null),
        };

    private static SdkPackageSessionSourceKind ToSdkSourceKind(PackageSourceKind sourceKind)
        => sourceKind switch
        {
            PackageSourceKind.Installed => SdkPackageSessionSourceKind.Installed,
            PackageSourceKind.Dev => SdkPackageSessionSourceKind.Dev,
            _ => SdkPackageSessionSourceKind.Installed,
        };

    private static SdkPackageSessionStatus? ToSdkStatus(Sunder.Runtime.Contracts.PackageSessionStatus? status)
        => status is null
            ? null
            : new SdkPackageSessionStatus(
                status.PackageId,
                status.DisplayName,
                status.Version,
                ToSdkSourceKind(status.ActiveSourceKind),
                status.IsLoaded,
                status.WatchEnabled,
                status.OverridesInstalledPackage,
                status.ErrorMessage);

}
