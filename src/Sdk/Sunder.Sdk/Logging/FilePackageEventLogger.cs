namespace Sunder.Sdk.Logging;

internal sealed class FilePackageEventLogger : IPackageEventLogger, IDisposable
{
    private readonly RollingSyslogLogWriter _writer;
    private readonly PackageLogResource _resource;

    public FilePackageEventLogger(
        string logsRootPath,
        PackageLogResource resource,
        TimeSpan retentionPeriod)
    {
        _writer = new RollingSyslogLogWriter(logsRootPath, "events", retentionPeriod);
        _resource = resource;
    }

    public ValueTask WriteAsync(
        PackageLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _writer.Write(PackageLogFormatter.CreateLine(
            timestamp: DateTimeOffset.UtcNow,
            level: level,
            source: "events",
            resource: _resource,
            category: null,
            eventName: eventName,
            message: message,
            attributes: attributes,
            exception: exception));
        return ValueTask.CompletedTask;
    }

    public void Dispose()
        => _writer.Dispose();
}
