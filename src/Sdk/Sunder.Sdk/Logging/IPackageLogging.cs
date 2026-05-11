using Microsoft.Extensions.Logging;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Logging;

[SunderSdkCapability(SunderSdkCapabilities.LoggingV1)]
public interface IPackageLogging
{
    ILoggerFactory LoggerFactory { get; }

    IPackageEventLogger Events { get; }
}
