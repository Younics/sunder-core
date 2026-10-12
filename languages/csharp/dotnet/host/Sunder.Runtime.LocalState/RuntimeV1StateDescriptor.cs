using System.Collections.Frozen;

namespace Sunder.Runtime.LocalState;

public sealed record RuntimeV1StateCategoryDescriptor(string Id, IReadOnlyList<string> RelativePaths)
{
    public IReadOnlyList<string> RelativePaths { get; } = Array.AsReadOnly(RelativePaths.ToArray());
}

public static class RuntimeV1StateDescriptor
{
    public const string RootProductDirectory = "Sunder";
    public const string RootRuntimeDirectory = "runtime";
    public const string RootVersionDirectory = "v1";
    public const string CatalogDirectory = "catalog";
    public const string InstalledPackageCatalogFile = "catalog/installed-packages.json";
    public const string RpcPermissionFile = "catalog/rpc-permissions.json";
    public const string PackagePolicyDirectory = "catalog/package-policy";
    public const string AppPolicySessionsDirectory = "catalog/app-policy-sessions";
    public const string InstalledDirectory = "installed";
    public const string StagingDirectory = "staging";
    public const string TransactionsDirectory = "transactions";
    public const string TombstonesDirectory = "tombstones";
    public const string PackageDataDirectory = "package-data";
    public const string TransfersDirectory = "transfers";
    public const string CacheDirectory = "cache";
    public const string PackageUiSnapshotCacheDirectory = "cache/package-ui-snapshots/r2";
    public const string PackageProjectionCacheFamilyDirectory = "cache/package-projections";
    public const string PackageProjectionCacheCoordinationFile = "cache/package-projections/coordination.lock";
    public const string PackageProjectionCacheDirectory = "cache/package-projections/r1";
    public const string ExecutionRuntimesDirectory = "execution-runtimes";
    public const string ExecutionRuntimeArtifactsDirectory = "execution-runtimes/artifacts";
    public const string ExecutionRuntimeArtifactSha256Directory = "execution-runtimes/artifacts/sha256";
    public const string ExecutionRuntimeRootsDirectory = "execution-runtimes/roots";
    public const string ExecutionRuntimeRootSha256Directory = "execution-runtimes/roots/sha256";
    public const string ExecutionRuntimeReceiptsDirectory = "execution-runtimes/receipts";
    public const string ExecutionRuntimeHealthDirectory = "execution-runtimes/health";
    public const string ExecutionRuntimeStagingDirectory = "execution-runtimes/staging";
    public const string ExecutionRuntimeTransactionsDirectory = "execution-runtimes/transactions";
    public const string ExecutionRuntimeQuarantineDirectory = "execution-runtimes/quarantine";
    public const string ExecutionRuntimeLeasesDirectory = "execution-runtimes/leases";
    public const string ExecutionRuntimeTombstonesDirectory = "execution-runtimes/tombstones";
    public const string ExecutionRuntimeManagementDirectory = "execution-runtimes/management";
    public const string ExecutionRuntimeManagementFile = "execution-runtimes/management/state.json";
    public const string CredentialsDirectory = "credentials";
    public const string RegistryCredentialNamespace = "registry";
    public const string RegistryCredentialsDirectory = "credentials/registry";
    public const string ConnectionFile = "connection.json";
    public const string ConnectionLockFile = "connection.json.lock";
    public const string ResetReceiptDirectory = "reset-receipts";
    public const string RegistryCredentialFile = "credentials.enc.json";
    public const string PackageSecretsKeyFile = "secrets.json.key";
    public const string RegistryCredentialsKeyFile = "credentials.enc.json.key";
    public const string MacOsMasterKeyService = "io.sunder.runtime.v1.package-storage.master-key";
    public const string LinuxMasterKeyAttribute = "sunder-runtime-v1-master-key";

    public static IReadOnlyList<RuntimeV1StateCategoryDescriptor> ResetCategories { get; } = Array.AsReadOnly<RuntimeV1StateCategoryDescriptor>(
    [
        new("installed-catalog-and-payloads", [InstalledPackageCatalogFile, InstalledDirectory, PackagePolicyDirectory]),
        new("package-transactions-and-derived-state", [StagingDirectory, TransactionsDirectory, TombstonesDirectory]),
        new("execution-runtime-derived-state", [
            ExecutionRuntimeArtifactsDirectory,
            ExecutionRuntimeRootsDirectory,
            ExecutionRuntimeReceiptsDirectory,
            ExecutionRuntimeHealthDirectory,
            ExecutionRuntimeStagingDirectory,
            ExecutionRuntimeTransactionsDirectory,
            ExecutionRuntimeQuarantineDirectory,
            ExecutionRuntimeLeasesDirectory,
            ExecutionRuntimeTombstonesDirectory,
            ExecutionRuntimeManagementDirectory,
        ]),
        new("uploads-and-snapshots", [TransfersDirectory, CacheDirectory]),
        new("runtime-connection", [ConnectionFile, ConnectionLockFile]),
    ]);

    public static RuntimeV1StateCategoryDescriptor StartupCacheResetCategory { get; } = new(
        "startup-cache",
        [PackageUiSnapshotCacheDirectory, PackageProjectionCacheDirectory, ExecutionRuntimeHealthDirectory]);

    public static IReadOnlySet<string> KnownRootEntries { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        CatalogDirectory,
        InstalledDirectory,
        StagingDirectory,
        TransactionsDirectory,
        TombstonesDirectory,
        PackageDataDirectory,
        TransfersDirectory,
        CacheDirectory,
        ExecutionRuntimesDirectory,
        CredentialsDirectory,
        ConnectionFile,
        ConnectionLockFile,
        ResetReceiptDirectory,
        RuntimeLocalState.SchemaFileName,
        RuntimeLocalState.LeaseFileName,
    }.ToFrozenSet(StringComparer.Ordinal);
}
