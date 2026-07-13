using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Controls activation sessions backed by packages in the Runtime installed-package store.</summary>
/// <remarks>Operations are serialized by the host. Cancellation stops waiting but does not guarantee rollback after activation starts.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.InstalledPackageSessionsV1)]
public interface IPackageInstalledSessionControl
{
    /// <summary>Loads the installed package identified by <paramref name="packageId"/>.</summary>
    Task<InstalledPackageSessionStatus> LoadInstalledPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    /// <summary>Unloads an active installed package, returning <see langword="false"/> when it is not active.</summary>
    Task<bool> UnloadInstalledPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    /// <summary>Gets installed-session status, or <see langword="null"/> when no installed session is known.</summary>
    Task<InstalledPackageSessionStatus?> GetInstalledPackageStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides an immutable snapshot of an installed package activation session.</summary>
/// <param name="PackageId">Canonical package identifier.</param>
/// <param name="DisplayName">User-facing name, or <see langword="null"/> before metadata is available.</param>
/// <param name="Version">Strict SemVer version, or <see langword="null"/> before metadata is available.</param>
/// <param name="IsLoaded">Whether package services and contributions from installed content are active.</param>
/// <param name="ErrorMessage">Activation failure text, or <see langword="null"/> when no failure is present.</param>
[SunderSdkCapability(SunderSdkCapabilities.InstalledPackageSessionsV1)]
public sealed record InstalledPackageSessionStatus(
    string PackageId,
    string? DisplayName,
    string? Version,
    bool IsLoaded,
    string? ErrorMessage);
