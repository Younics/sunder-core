using Sunder.Runtime.Host.Infrastructure.Logging;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageContext : IPackageContext
{
    public RuntimePackageContext(string packageId, string version, string contentRootPath, string packageDataRootPath)
    {
        PackageId = packageId;
        Version = version;
        ContentRootPath = contentRootPath;
        LocalStorage = new LocalPackageStorageContext(packageId, packageDataRootPath);
        Storage = LocalStorage;
        PackageSettings = new PackageSettings(LocalStorage.SettingsStore);
        Settings = PackageSettings;
        SecretsStore = new JsonPackageSecretsStore(Path.Combine(LocalStorage.DataRootPath, "secrets.json"));
        Logging = new FilePackageLogging(LocalStorage.LogsRootPath, PackageId, Version);
    }

    public string PackageId { get; }

    public string Version { get; }

    public string ContentRootPath { get; }

    public IPackageStorageContext Storage { get; }

    internal LocalPackageStorageContext LocalStorage { get; }

    public IPackageSettings Settings { get; }

    internal PackageSettings PackageSettings { get; }

    public IPackageSecrets Secrets => SecretsStore;

    public JsonPackageSecretsStore SecretsStore { get; }

    public IPackageCallbackClient Callbacks => NullPackageCallbackClient.Instance;

    public IPackageLogging Logging { get; }
}
