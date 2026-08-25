using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sunder.Sdk.Logging;

namespace Sunder.Sdk.Worker.Internal;

internal sealed class WorkerPackageLogging : IPackageLogging
{
    public WorkerPackageLogging(WorkerRpcClient client)
    {
        LoggerFactory = new WorkerLoggerFactory(client);
        Events = new WorkerPackageEventLogger(client);
    }

    public ILoggerFactory LoggerFactory { get; }

    public IPackageEventLogger Events { get; }
}

internal sealed class WorkerPackageEventLogger(WorkerRpcClient client) : IPackageEventLogger
{
    public ValueTask WriteAsync(
        PackageLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = WorkerLogEntry.CreateEvent(level, eventName, message, attributes, exception);
        return client.WritePackageLogAsync(entry, cancellationToken);
    }
}

internal sealed class WorkerLoggerFactory(WorkerRpcClient client) : ILoggerFactory
{
    public void AddProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        throw new NotSupportedException("The worker Host-mediated logger factory does not accept local providers.");
    }

    public ILogger CreateLogger(string categoryName)
        => new WorkerLogger(client, WorkerLogEntry.ValidateRequiredText(categoryName, nameof(categoryName), 256));

    public void Dispose()
    {
    }
}

internal sealed class WorkerLogger(WorkerRpcClient client, string category) : ILogger
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull
        => WorkerLoggerScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        ArgumentNullException.ThrowIfNull(formatter);
        try
        {
            var message = formatter(state, exception);
            var attributes = state as IEnumerable<KeyValuePair<string, object?>>;
            var entry = WorkerLogEntry.CreateConventional(
                logLevel,
                category,
                eventId,
                message,
                attributes,
                exception);
            client.WritePackageLogDetached(entry);
        }
        catch
        {
            // Conventional logging must not fault package execution.
        }
    }

    private sealed class WorkerLoggerScope : IDisposable
    {
        public static WorkerLoggerScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

internal sealed record WorkerLogEntry(
    string Channel,
    PackageLogLevel Level,
    string? Category,
    int EventId,
    string? EventName,
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> Attributes,
    IReadOnlyList<WorkerLogException> Exceptions)
{
    private const int MaximumAttributes = 64;
    private const int MaximumAttributeKeyCharacters = 128;
    private const int MaximumAttributeValueCharacters = 4096;
    private const int MaximumMessageCharacters = 16384;
    private const int MaximumExceptionDepth = 5;

    public static WorkerLogEntry CreateEvent(
        PackageLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes,
        Exception? exception)
    {
        if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
        return new WorkerLogEntry(
            "event",
            level,
            null,
            0,
            ValidateRequiredText(eventName, nameof(eventName), 256),
            ValidateText(message, nameof(message), MaximumMessageCharacters),
            NormalizeAttributes(attributes),
            CaptureExceptions(exception));
    }

    public static WorkerLogEntry CreateConventional(
        LogLevel level,
        string category,
        EventId eventId,
        string message,
        IEnumerable<KeyValuePair<string, object?>>? attributes,
        Exception? exception)
    {
        if (level is < LogLevel.Trace or > LogLevel.Critical)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }
        return new WorkerLogEntry(
            "logger",
            ToPackageLogLevel(level),
            category,
            eventId.Id,
            NormalizeOptionalText(eventId.Name, 256),
            ValidateText(message, nameof(message), MaximumMessageCharacters),
            NormalizeAttributes(attributes),
            CaptureExceptions(exception));
    }

    public static string ValidateRequiredText(string value, string parameterName, int maximumCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return ValidateText(value, parameterName, maximumCharacters);
    }

    private static string ValidateText(string value, string parameterName, int maximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length > maximumCharacters)
        {
            throw new ArgumentException(
                $"The value cannot exceed {maximumCharacters} characters.",
                parameterName);
        }
        return value;
    }

    private static string? NormalizeOptionalText(string? value, int maximumCharacters)
        => value is null ? null : Truncate(value, maximumCharacters);

    private static IReadOnlyList<KeyValuePair<string, object?>> NormalizeAttributes(
        IEnumerable<KeyValuePair<string, object?>>? attributes)
    {
        if (attributes is null) return [];
        var normalized = new List<KeyValuePair<string, object?>>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in attributes)
        {
            if (string.Equals(attribute.Key, "{OriginalFormat}", StringComparison.Ordinal)) continue;
            var key = ValidateRequiredText(
                attribute.Key,
                nameof(attributes),
                MaximumAttributeKeyCharacters);
            if (!names.Add(key)) continue;
            if (normalized.Count == MaximumAttributes)
            {
                throw new ArgumentException(
                    $"Logging supports at most {MaximumAttributes} attributes per entry.",
                    nameof(attributes));
            }
            normalized.Add(new KeyValuePair<string, object?>(key, NormalizeAttributeValue(attribute.Value)));
        }
        return normalized;
    }

    private static object? NormalizeAttributeValue(object? value)
        => value switch
        {
            null => null,
            string text => Truncate(text, MaximumAttributeValueCharacters),
            char character => character.ToString(),
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal => value,
            float number when float.IsFinite(number) => number,
            double number when double.IsFinite(number) => number,
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("D"),
            Enum enumeration => Truncate(enumeration.ToString(), MaximumAttributeValueCharacters),
            JsonElement element => NormalizeJsonAttributeValue(element),
            IFormattable formattable => Truncate(
                formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
                MaximumAttributeValueCharacters),
            _ => Truncate(value.ToString() ?? string.Empty, MaximumAttributeValueCharacters),
        };

    private static object? NormalizeJsonAttributeValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => Truncate(value.GetString() ?? string.Empty, MaximumAttributeValueCharacters),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.Number => value.GetDouble(),
            _ => Truncate(value.GetRawText(), MaximumAttributeValueCharacters),
        };

    private static IReadOnlyList<WorkerLogException> CaptureExceptions(Exception? exception)
    {
        if (exception is null) return [];
        var exceptions = new List<WorkerLogException>(MaximumExceptionDepth);
        for (var current = exception;
             current is not null && exceptions.Count < MaximumExceptionDepth;
             current = current.InnerException)
        {
            exceptions.Add(new WorkerLogException(
                Truncate(current.GetType().FullName ?? current.GetType().Name, 512),
                Truncate(current.Message, 4096),
                current.StackTrace is null ? null : Truncate(current.StackTrace, 32768)));
        }
        return exceptions;
    }

    private static PackageLogLevel ToPackageLogLevel(LogLevel level)
        => level switch
        {
            LogLevel.Trace => PackageLogLevel.Trace,
            LogLevel.Debug => PackageLogLevel.Debug,
            LogLevel.Information => PackageLogLevel.Information,
            LogLevel.Warning => PackageLogLevel.Warning,
            LogLevel.Error => PackageLogLevel.Error,
            LogLevel.Critical => PackageLogLevel.Critical,
            _ => throw new ArgumentOutOfRangeException(nameof(level)),
        };

    private static string Truncate(string value, int maximumCharacters)
    {
        if (value.Length <= maximumCharacters) return value;
        var length = maximumCharacters;
        if (char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length])) length--;
        return value[..length];
    }
}

internal sealed record WorkerLogException(string Type, string Message, string? StackTrace);
