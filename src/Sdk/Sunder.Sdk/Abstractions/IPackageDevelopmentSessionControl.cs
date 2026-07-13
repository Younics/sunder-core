using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Optionally controls package activations from host-local development output.</summary>
[SunderSdkCapability(SunderSdkCapabilities.DevelopmentPackageSessionsV1)]
public interface IPackageDevelopmentSessionControl
{
    /// <summary>Gets whether development-session operations are available and, when unavailable, why.</summary>
    PackageDevelopmentSessionAvailability Availability { get; }

    /// <summary>Loads host-local development output and returns a structured operation outcome.</summary>
    Task<PackageDevelopmentSessionOperationResult> LoadDevelopmentPackageAsync(
        PackageDevelopmentSessionLoadRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Unloads a development activation and returns a structured operation outcome.</summary>
    Task<PackageDevelopmentSessionOperationResult> UnloadDevelopmentPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    /// <summary>Gets development-session status, or <see langword="null"/> when no development activation is known.</summary>
    Task<PackageDevelopmentSessionStatus?> GetDevelopmentPackageStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default);
}

/// <summary>Describes development-session capability availability.</summary>
/// <param name="IsAvailable">Whether operations may be attempted.</param>
/// <param name="UnavailableReason">User-facing reason when operations are unavailable.</param>
[SunderSdkCapability(SunderSdkCapabilities.DevelopmentPackageSessionsV1)]
public sealed record PackageDevelopmentSessionAvailability(bool IsAvailable, string? UnavailableReason);

/// <summary>Requests activation from a directory visible to the host that implements the development capability.</summary>
/// <param name="DevelopmentOutputPath">Host-local path to generated <c>sunder-dev</c> output.</param>
/// <param name="Watch">Whether the host should reload after output changes.</param>
[SunderSdkCapability(SunderSdkCapabilities.DevelopmentPackageSessionsV1)]
public sealed record PackageDevelopmentSessionLoadRequest(string DevelopmentOutputPath, bool Watch = false);

/// <summary>Describes the outcome of a development-session operation.</summary>
/// <param name="Outcome">Machine-readable outcome.</param>
/// <param name="Message">User-facing outcome detail.</param>
/// <param name="Status">Resulting status when available.</param>
[SunderSdkCapability(SunderSdkCapabilities.DevelopmentPackageSessionsV1)]
public sealed record PackageDevelopmentSessionOperationResult(
    PackageDevelopmentSessionOperationOutcome Outcome,
    string Message,
    PackageDevelopmentSessionStatus? Status = null)
{
    /// <summary>Gets whether the requested operation succeeded.</summary>
    public bool IsSuccess => Outcome == PackageDevelopmentSessionOperationOutcome.Succeeded;
}

/// <summary>Specifies a development-session operation outcome.</summary>
[SunderSdkCapability(SunderSdkCapabilities.DevelopmentPackageSessionsV1)]
public enum PackageDevelopmentSessionOperationOutcome
{
    /// <summary>The operation succeeded.</summary>
    Succeeded = 0,
    /// <summary>The requested development activation was not active.</summary>
    NotActive = 1,
    /// <summary>The current host topology does not support the operation.</summary>
    Unsupported = 2,
    /// <summary>The operation was supported but failed.</summary>
    Failed = 3,
}

/// <summary>Provides an immutable snapshot of a development package activation session.</summary>
/// <param name="PackageId">Canonical package identifier.</param>
/// <param name="DisplayName">User-facing name, or <see langword="null"/> before metadata is available.</param>
/// <param name="Version">Strict SemVer version, or <see langword="null"/> before metadata is available.</param>
/// <param name="IsLoaded">Whether package services and contributions from development output are active.</param>
/// <param name="WatchEnabled">Whether output watching is active.</param>
/// <param name="OverridesInstalledPackage">Whether development output shadows an installed package.</param>
/// <param name="ErrorMessage">Activation failure text, or <see langword="null"/> when no failure is present.</param>
[SunderSdkCapability(SunderSdkCapabilities.DevelopmentPackageSessionsV1)]
public sealed record PackageDevelopmentSessionStatus(
    string PackageId,
    string? DisplayName,
    string? Version,
    bool IsLoaded,
    bool WatchEnabled,
    bool OverridesInstalledPackage,
    string? ErrorMessage);

/// <summary>Provides explicit unavailable development-session behavior for host roles without the capability.</summary>
[SunderSdkCapability(SunderSdkCapabilities.DevelopmentPackageSessionsV1)]
public sealed class UnavailablePackageDevelopmentSessionControl : IPackageDevelopmentSessionControl
{
    private const string Reason = "Development package session control is unavailable in the current host context.";

    /// <summary>Gets the shared stateless instance.</summary>
    public static UnavailablePackageDevelopmentSessionControl Instance { get; } = new();

    private UnavailablePackageDevelopmentSessionControl()
    {
    }

    /// <inheritdoc />
    public PackageDevelopmentSessionAvailability Availability { get; } = new(false, Reason);

    /// <inheritdoc />
    public Task<PackageDevelopmentSessionOperationResult> LoadDevelopmentPackageAsync(
        PackageDevelopmentSessionLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Unsupported());
    }

    /// <inheritdoc />
    public Task<PackageDevelopmentSessionOperationResult> UnloadDevelopmentPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Unsupported());
    }

    /// <inheritdoc />
    public Task<PackageDevelopmentSessionStatus?> GetDevelopmentPackageStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<PackageDevelopmentSessionStatus?>(null);
    }

    private static PackageDevelopmentSessionOperationResult Unsupported()
        => new(PackageDevelopmentSessionOperationOutcome.Unsupported, Reason);
}
