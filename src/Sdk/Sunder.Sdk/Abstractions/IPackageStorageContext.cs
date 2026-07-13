using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Groups host-owned storage capabilities for one package activation and host role.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
public interface IPackageStorageContext
{
    /// <summary>Gets the package file store.</summary>
    IPackageFileStore Files { get; }

    /// <summary>Gets the package key/value store.</summary>
    IPackageKeyValueStore State { get; }

    /// <summary>
    /// Gets the package-scoped workspace owned by the current activation and host role.
    /// This capability is intended only for APIs that require local paths, including SQLite,
    /// process working directories, container mounts, and atomic directory trees.
    /// It is unavailable during package preflight and on non-local Runtime transports.
    /// </summary>
    IPackageRoleLocalWorkspace RoleLocalWorkspace { get; }
}
