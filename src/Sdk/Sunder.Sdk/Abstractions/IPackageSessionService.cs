using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Controls App-hosted installed and development package activation sessions.</summary>
/// <remarks>Operations are asynchronous and serialized by the host. Cancellation stops waiting but does not guarantee rollback after activation has started.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.PackageSessionsV1)]
public interface IPackageSessionService
{
    /// <summary>Loads or replaces a package session and returns its resulting status.</summary>
    Task<PackageSessionStatus> LoadPackageAsync(
        PackageSessionLoadRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Unloads the matching active source, returning <see langword="false"/> when it is not active.</summary>
    Task<bool> UnloadPackageAsync(
        string packageId,
        PackageSessionSourceKind sourceKind,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the current package status, or <see langword="null"/> when no session is known.</summary>
    Task<PackageSessionStatus?> GetPackageStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default);
}

/// <summary>Identifies a package source to activate.</summary>
/// <param name="SourceKind">Whether the source is installed state or a development directory.</param>
/// <param name="Source">Host-defined installed identity or absolute local development package path.</param>
/// <param name="Watch">Whether a development source should reload after file changes; defaults to disabled.</param>
[SunderSdkCapability(SunderSdkCapabilities.PackageSessionsV1)]
public sealed record PackageSessionLoadRequest(
    PackageSessionSourceKind SourceKind,
    string Source,
    bool Watch = false);

/// <summary>Provides an immutable snapshot of one package activation session.</summary>
/// <param name="PackageId">Runtime package identifier.</param>
/// <param name="DisplayName">User-facing name, or <see langword="null"/> before metadata is available.</param>
/// <param name="Version">Strict SemVer version, or <see langword="null"/> before metadata is available.</param>
/// <param name="ActiveSourceKind">Source currently selected by the host.</param>
/// <param name="IsLoaded">Whether package services and contributions are active.</param>
/// <param name="WatchEnabled">Whether development-source file watching is active.</param>
/// <param name="OverridesInstalledPackage">Whether a development source currently shadows an installed package.</param>
/// <param name="ErrorMessage">Activation failure text, or <see langword="null"/> when no failure is present.</param>
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

/// <summary>Specifies where an activation session obtains package content.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageSessionsV1)]
public enum PackageSessionSourceKind
{
    /// <summary>Uses immutable content managed by the Runtime package store.</summary>
    Installed = 0,
    /// <summary>Uses a local unpacked package directory for development.</summary>
    Dev = 1,
}

/// <summary>Represents hosts that do not expose package-session control.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageSessionsV1)]
public sealed class NullPackageSessionService : IPackageSessionService
{
    /// <summary>Gets the shared stateless instance.</summary>
    public static NullPackageSessionService Instance { get; } = new();

    private NullPackageSessionService()
    {
    }

    /// <summary>Returns a faulted task because loading is unavailable.</summary>
    public Task<PackageSessionStatus> LoadPackageAsync(
        PackageSessionLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<PackageSessionStatus>(new NotSupportedException("Package session loading is not available in this host context."));
    }

    /// <summary>Returns <see langword="false"/> after observing cancellation.</summary>
    public Task<bool> UnloadPackageAsync(
        string packageId,
        PackageSessionSourceKind sourceKind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    /// <summary>Returns <see langword="null"/> after observing cancellation.</summary>
    public Task<PackageSessionStatus?> GetPackageStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<PackageSessionStatus?>(null);
    }
}
