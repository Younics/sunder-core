using Microsoft.Extensions.Logging;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Logging;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.CoreV1)]
public interface IPackageContext
{
    string PackageId { get; }
    Version Version { get; }
    string InstallPath { get; }
    IPackageStorageContext Storage { get; }
    IPackageConfiguration Configuration { get; }
    IPackageSecrets Secrets { get; }
    ILoggerFactory LoggerFactory { get; }
    IPackageLogging Logging { get; }
}
