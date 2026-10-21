using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Worker;

/// <summary>Prepares package-local migration state through the read-only Worker preparation barrier.</summary>
/// <remarks>
/// Generated composition invokes concrete source implementations in constructor
/// dependency order after generation commit and before any declared migration or
/// activation callback. The context grants read-only state access, not secret
/// enumeration, persistent mutation, workspace access, or RPC authority.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
[SunderSdkCapability(SunderSdkCapabilities.StorageKeyMigrationV1)]
public interface ISunderWorkerStorageMigrationPreparation
{
    /// <summary>Prepares the exact committed generation without modifying persistent data.</summary>
    ValueTask PrepareStorageMigrationsAsync(
        SunderWorkerMigrationPreparationContext context,
        CancellationToken cancellationToken = default);
}
