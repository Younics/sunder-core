namespace Sunder.Sdk.Logging;

public static class PackageEventLoggerExtensions
{
    public static ValueTask TraceAsync(
        this IPackageEventLogger logger,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        CancellationToken cancellationToken = default)
        => logger.WriteAsync(PackageLogLevel.Trace, eventName, message, attributes, cancellationToken: cancellationToken);

    public static ValueTask DebugAsync(
        this IPackageEventLogger logger,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        CancellationToken cancellationToken = default)
        => logger.WriteAsync(PackageLogLevel.Debug, eventName, message, attributes, cancellationToken: cancellationToken);

    public static ValueTask InformationAsync(
        this IPackageEventLogger logger,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        CancellationToken cancellationToken = default)
        => logger.WriteAsync(PackageLogLevel.Information, eventName, message, attributes, cancellationToken: cancellationToken);

    public static ValueTask WarningAsync(
        this IPackageEventLogger logger,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
        => logger.WriteAsync(PackageLogLevel.Warning, eventName, message, attributes, exception, cancellationToken);

    public static ValueTask ErrorAsync(
        this IPackageEventLogger logger,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
        => logger.WriteAsync(PackageLogLevel.Error, eventName, message, attributes, exception, cancellationToken);

    public static ValueTask CriticalAsync(
        this IPackageEventLogger logger,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
        => logger.WriteAsync(PackageLogLevel.Critical, eventName, message, attributes, exception, cancellationToken);
}
