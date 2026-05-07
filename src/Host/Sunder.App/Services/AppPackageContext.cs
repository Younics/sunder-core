using Microsoft.Extensions.Logging;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Storage;

namespace Sunder.App.Services;

internal sealed class AppPackageContext : IPackageContext
{
    public AppPackageContext(string packageId, string version, string installPath)
    {
        PackageId = packageId;
        Version = Version.TryParse(version, out var parsedVersion) ? parsedVersion : new Version(0, 0);
        InstallPath = installPath;
        Storage = new LocalPackageStorageContext(packageId);
        Configuration = new PackageStateConfiguration(Storage.State);
        Secrets = new JsonPackageSecretsStore(Path.Combine(Storage.DataRootPath, "secrets.json"));
        Logging = new FilePackageLogging(Storage.LogsRootPath, PackageId, Version);
    }

    public string PackageId { get; }

    public Version Version { get; }

    public string InstallPath { get; }

    public IPackageStorageContext Storage { get; }

    public IPackageConfiguration Configuration { get; }

    public IPackageSecrets Secrets { get; }

    public ILoggerFactory LoggerFactory => Logging.LoggerFactory;

    public IPackageLogging Logging { get; }
}
