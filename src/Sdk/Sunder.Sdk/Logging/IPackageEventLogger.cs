using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Logging;

[SunderSdkCapability(SunderSdkCapabilities.LoggingV1)]
public interface IPackageEventLogger
{
    ValueTask WriteAsync(
        PackageLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default);
}
