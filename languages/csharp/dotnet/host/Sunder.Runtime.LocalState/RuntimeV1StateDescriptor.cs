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
    public const string InstalledDirectory = "installed";
    public const string StagingDirectory = "staging";
    public const string TransactionsDirectory = "transactions";
    public const string TombstonesDirectory = "tombstones";
    public const string PackageDataDirectory = "package-data";
    public const string TransfersDirectory = "transfers";
    public const string CacheDirectory = "cache";
    public const string CredentialsDirectory = "credentials";
    public const string RegistryCredentialNamespace = "registry";
    public const string RegistryCredentialsDirectory = "credentials/registry";
    public const string ConnectionFile = "connection.json";
    public const string ConnectionLockFile = "connection.json.lock";
    public const string RegistryCredentialFile = "credentials.enc.json";
    public const string PackageSecretsKeyFile = "secrets.json.key";
    public const string RegistryCredentialsKeyFile = "credentials.enc.json.key";
    public const string MacOsMasterKeyService = "io.sunder.runtime.v1.package-storage.master-key";
    public const string LinuxMasterKeyAttribute = "sunder-runtime-v1-master-key";

    public static IReadOnlyList<RuntimeV1StateCategoryDescriptor> ResetCategories { get; } = Array.AsReadOnly<RuntimeV1StateCategoryDescriptor>(
    [
        new("package-catalog-and-payloads", [CatalogDirectory, InstalledDirectory, StagingDirectory, TransactionsDirectory, TombstonesDirectory]),
        new("package-state-files-secrets-and-logs", [PackageDataDirectory]),
        new("uploads-and-snapshots", [TransfersDirectory, CacheDirectory]),
        new("registry-credentials", [CredentialsDirectory]),
        new("runtime-connection", [ConnectionFile, ConnectionLockFile]),
        new("runtime-v1-root", []),
    ]);

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
        CredentialsDirectory,
        ConnectionFile,
        ConnectionLockFile,
        RuntimeLocalState.SchemaFileName,
        RuntimeLocalState.LeaseFileName,
    }.ToFrozenSet(StringComparer.Ordinal);
}
