using Microsoft.Extensions.Logging;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Logging;

/// <summary>Groups host-owned conventional and structured logging for one package activation.</summary>
[SunderSdkCapability(SunderSdkCapabilities.LoggingV1)]
public interface IPackageLogging
{
    /// <summary>Gets the package-scoped Microsoft logger factory.</summary>
    ILoggerFactory LoggerFactory { get; }

    /// <summary>Gets the thread-safe structured event logger.</summary>
    IPackageEventLogger Events { get; }
}
