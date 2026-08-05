using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Logging;

/// <summary>Writes structured events to host-owned package logging.</summary>
/// <remarks>Implementations are thread-safe and copy attributes before returning. Cancellation may prevent persistence but never transfers ownership of supplied values.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.LoggingV1)]
public interface IPackageEventLogger
{
    /// <summary>Writes one named event with optional structured attributes and exception details.</summary>
    ValueTask WriteAsync(
        PackageLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default);
}
