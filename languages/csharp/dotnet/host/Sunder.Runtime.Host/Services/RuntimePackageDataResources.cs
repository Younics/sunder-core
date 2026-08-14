using Sunder.Runtime.Host.Infrastructure.Logging;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageDataResources : IDisposable
{
    public RuntimePackageDataResources(
        string packageId,
        string version,
        string packageDataRootPath)
    {
        Storage = new LocalPackageStorageContext(packageId, packageDataRootPath);
        Settings = new PackageSettings(Storage.SettingsStore);
        Secrets = new JsonPackageSecretsStore(Path.Combine(Storage.DataRootPath, "secrets.json"));
        MigrationJournal = new PackageStorageMigrationJournal(
            Path.Combine(Storage.DataRootPath, "storage-migrations.json"));
        Logging = new FilePackageLogging(Storage.LogsRootPath, packageId, version);
    }

    public LocalPackageStorageContext Storage { get; }

    public PackageSettings Settings { get; }

    public JsonPackageSecretsStore Secrets { get; }

    public PackageStorageMigrationJournal MigrationJournal { get; }

    public IPackageLogging Logging { get; }

    public void Dispose()
    {
        if (Logging is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
