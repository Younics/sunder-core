using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Storage;

namespace Sunder.Sdk.Worker;

/// <summary>Supplies immutable storage-key migration plans for generated Worker composition.</summary>
/// <remarks>
/// A package may declare one concrete source implementation. Plans are read and
/// snapshotted before worker.ready. Construction and getters must not perform Host
/// operations or mutate package data. Dynamic rule delegates may use local state
/// initialized by an ISunderWorkerStorageMigrationPreparation implementation.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
[SunderSdkCapability(SunderSdkCapabilities.StorageKeyMigrationV1)]
public interface ISunderWorkerStorageMigrationProvider
{
    /// <summary>Gets state-key migrations to apply before activation; empty by default.</summary>
    IReadOnlyList<PackageStorageKeyMigration> StateMigrations => [];

    /// <summary>Gets secret-key migrations to apply before activation; empty by default.</summary>
    IReadOnlyList<PackageStorageKeyMigration> SecretMigrations => [];
}
