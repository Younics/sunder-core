namespace Sunder.Sdk.Logging;

/// <summary>Provides severity-specific structured event logging helpers.</summary>
public static class PackageEventLoggerExtensions
{
    /// <summary>Writes diagnostic detail normally retained only at the most verbose level.</summary>
    public static ValueTask TraceAsync(
        this IPackageEventLogger logger,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        CancellationToken cancellationToken = default)
        => logger.WriteAsync(PackageLogLevel.Trace, eventName, message, attributes, cancellationToken: cancellationToken);

    /// <summary>Writes developer-oriented diagnostic state.</summary>
    public static ValueTask DebugAsync(
        this IPackageEventLogger logger,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        CancellationToken cancellationToken = default)
        => logger.WriteAsync(PackageLogLevel.Debug, eventName, message, attributes, cancellationToken: cancellationToken);

    /// <summary>Writes a normal package lifecycle or operation event.</summary>
    public static ValueTask InformationAsync(
        this IPackageEventLogger logger,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        CancellationToken cancellationToken = default)
        => logger.WriteAsync(PackageLogLevel.Information, eventName, message, attributes, cancellationToken: cancellationToken);

    /// <summary>Writes a recoverable problem with optional exception details.</summary>
    public static ValueTask WarningAsync(
        this IPackageEventLogger logger,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
        => logger.WriteAsync(PackageLogLevel.Warning, eventName, message, attributes, exception, cancellationToken);

    /// <summary>Writes an operation failure with optional exception details.</summary>
    public static ValueTask ErrorAsync(
        this IPackageEventLogger logger,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
        => logger.WriteAsync(PackageLogLevel.Error, eventName, message, attributes, exception, cancellationToken);

    /// <summary>Writes a failure that prevents safe package operation.</summary>
    public static ValueTask CriticalAsync(
        this IPackageEventLogger logger,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
        => logger.WriteAsync(PackageLogLevel.Critical, eventName, message, attributes, exception, cancellationToken);
}
