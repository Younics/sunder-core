using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Storage;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides host-atomic migration and cleanup of package-owned legacy physical keys.</summary>
/// <remarks>
/// Runtime-backed state and secret stores implement this capability. Rules are evaluated ordinally. A migration
/// retains the prior document, is idempotent, and rejects ambiguous or nonidentical destination collisions.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
[SunderSdkCapability(SunderSdkCapabilities.StorageKeyMigrationV1)]
public interface IPackageStorageKeyMigrator
{
    /// <summary>Atomically applies the declared legacy-key migrations before normal storage access.</summary>
    /// <exception cref="ArgumentException"><paramref name="migrations"/> is null, empty, or contains a null rule.</exception>
    /// <exception cref="InvalidDataException">A stored invalid key is not covered by exactly one migration rule.</exception>
    /// <exception cref="InvalidOperationException">Rules are ambiguous or destination values collide.</exception>
    Task MigrateKeysAsync(
        IReadOnlyList<PackageStorageKeyMigration> migrations,
        CancellationToken cancellationToken = default);
}
