using Microsoft.Extensions.Logging;
using Sunder.Runtime.Host.Infrastructure.Logging;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageContext : IPackageContext
{
    public RuntimePackageContext(string packageId, string version, string installPath, string packageDataRootPath)
    {
        PackageId = packageId;
        Version = version;
        InstallPath = installPath;
        LocalStorage = new LocalPackageStorageContext(packageId, packageDataRootPath);
        Storage = LocalStorage;
        Configuration = new PackageStateConfiguration(Storage.State);
        SecretsStore = new JsonPackageSecretsStore(Path.Combine(LocalStorage.DataRootPath, "secrets.json"));
        Logging = new FilePackageLogging(LocalStorage.LogsRootPath, PackageId, Version);
    }

    public string PackageId { get; }

    public string Version { get; }

    public string InstallPath { get; }

    public IPackageStorageContext Storage { get; }

    internal LocalPackageStorageContext LocalStorage { get; }

    public IPackageConfiguration Configuration { get; }

    public IPackageSecrets Secrets => SecretsStore;

    public JsonPackageSecretsStore SecretsStore { get; }

    public ILoggerFactory LoggerFactory => Logging.LoggerFactory;

    public IPackageLogging Logging { get; }
}
