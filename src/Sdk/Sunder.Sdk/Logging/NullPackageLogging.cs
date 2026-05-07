using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sunder.Sdk.Logging;

public sealed class NullPackageLogging : IPackageLogging
{
    public static NullPackageLogging Instance { get; } = new();

    private NullPackageLogging()
    {
    }

    public ILoggerFactory LoggerFactory { get; } = NullLoggerFactory.Instance;

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
