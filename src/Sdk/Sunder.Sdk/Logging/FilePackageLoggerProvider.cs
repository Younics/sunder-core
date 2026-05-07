using Microsoft.Extensions.Logging;

namespace Sunder.Sdk.Logging;

internal sealed class FilePackageLoggerProvider : ILoggerProvider
{
    private readonly RollingSyslogLogWriter _writer;
    private readonly PackageLogResource _resource;

    public FilePackageLoggerProvider(
        string logsRootPath,
        string filePrefix,
        PackageLogResource resource,
        TimeSpan retentionPeriod)
    {
        _writer = new RollingSyslogLogWriter(logsRootPath, filePrefix, retentionPeriod);
        _resource = resource;
    }

    public ILogger CreateLogger(string categoryName)
        => new FilePackageLogger(_writer, _resource, categoryName);

    public void Dispose()
        => _writer.Dispose();

    private sealed class FilePackageLogger(
        RollingSyslogLogWriter writer,
        PackageLogResource resource,
        string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var attributes = PackageLogFormatter.ExtractAttributes(state);
            if (eventId.Id != 0)
            {
                attributes["event.id"] = eventId.Id;
            }

            if (!string.IsNullOrWhiteSpace(eventId.Name))
            {
                attributes["event.name"] = eventId.Name;
            }

            writer.Write(PackageLogFormatter.CreateLine(
                timestamp: DateTimeOffset.UtcNow,
                level: PackageLogFormatter.ToPackageLogLevel(logLevel),
                source: "runtime",
                resource: resource,
                category: categoryName,
                eventName: eventId.Name,
                message: formatter(state, exception),
                attributes: attributes,
                exception: exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
