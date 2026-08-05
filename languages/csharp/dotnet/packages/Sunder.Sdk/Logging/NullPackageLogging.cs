using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sunder.Sdk.Logging;

/// <summary>Discards package logs for host contexts where logging is unavailable.</summary>
public sealed class NullPackageLogging : IPackageLogging
{
    /// <summary>Gets the shared stateless instance.</summary>
    public static NullPackageLogging Instance { get; } = new();

    private NullPackageLogging()
    {
    }

    /// <summary>Gets a logger factory whose loggers discard all messages.</summary>
    public ILoggerFactory LoggerFactory { get; } = NullLoggerFactory.Instance;

    /// <summary>Gets an event logger that observes cancellation and otherwise discards events.</summary>
    public IPackageEventLogger Events { get; } = NullPackageEventLogger.Instance;

    private sealed class NullPackageEventLogger : IPackageEventLogger
    {
        public static NullPackageEventLogger Instance { get; } = new();

        public ValueTask WriteAsync(
            PackageLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
