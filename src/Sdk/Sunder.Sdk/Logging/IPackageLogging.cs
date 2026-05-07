using Microsoft.Extensions.Logging;

namespace Sunder.Sdk.Logging;

public interface IPackageLogging
{
    ILoggerFactory LoggerFactory { get; }

    IPackageEventLogger Events { get; }
}
