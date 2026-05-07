using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Sunder.Sdk.Logging;

internal static class PackageLogFormatter
{
    private static readonly string[] SensitiveKeyFragments =
    [
        "apikey",
        "api_key",
        "authorization",
        "cookie",
        "password",
        "refresh",
        "secret",
        "token",
    ];

    private static readonly string HostName = string.IsNullOrWhiteSpace(Environment.MachineName)
        ? "localhost"
        : Environment.MachineName;

    private static readonly int ProcessId = Environment.ProcessId;

    public static string CreateLine(
        DateTimeOffset timestamp,
        PackageLogLevel level,
        string source,
        PackageLogResource resource,
        string? category,
        string? eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes,
        Exception? exception)
    {
        var fields = SanitizeAttributes(attributes, source, category, eventName);
        fields["package_version"] = resource.PackageVersion;
        if (exception is not null)
        {
            AddExceptionFields(fields, "exception", exception);
        }

        var builder = new StringBuilder()
            .Append(timestamp.ToLocalTime().ToString("MMM dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(HostName)
            .Append(' ')
            .Append(SanitizeTag(resource.PackageId))
            .Append('[')
            .Append(ProcessId.ToString(CultureInfo.InvariantCulture))
            .Append("]: level=")
            .Append(ToSeverityText(level))
            .Append(" event=")
            .Append(FormatFieldValue(string.IsNullOrWhiteSpace(eventName) ? "-" : eventName))
            .Append(" msg=")
            .Append(FormatFieldValue(message));

        foreach (var field in fields.OrderBy(field => field.Key, StringComparer.Ordinal))
        {
            builder.Append(' ')
                .Append(field.Key)
                .Append('=')
                .Append(FormatFieldValue(field.Value));
        }

        return builder.ToString();
    }

    public static PackageLogLevel ToPackageLogLevel(LogLevel level)
        => level switch
        {
            LogLevel.Trace => PackageLogLevel.Trace,
            LogLevel.Debug => PackageLogLevel.Debug,
            LogLevel.Information => PackageLogLevel.Information,
            LogLevel.Warning => PackageLogLevel.Warning,
            LogLevel.Error => PackageLogLevel.Error,
            LogLevel.Critical => PackageLogLevel.Critical,
            _ => PackageLogLevel.Information,
        };

    public static Dictionary<string, object?> ExtractAttributes<TState>(TState state)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
        {
            foreach (var item in values)
            {
                if (string.Equals(item.Key, "{OriginalFormat}", StringComparison.Ordinal))
                {
                    attributes["log.original_format"] = item.Value?.ToString();
                    continue;
                }

                attributes[item.Key] = item.Value;
            }
        }

        return attributes;
    }

    private static Dictionary<string, object?> SanitizeAttributes(
        IReadOnlyDictionary<string, object?>? attributes,
        string source,
        string? category,
        string? eventName)
    {
        var sanitized = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = source,
        };
        if (!string.IsNullOrWhiteSpace(category))
        {
            sanitized["category"] = category;
        }

        if (attributes is null)
        {
            return sanitized;
        }

        foreach (var item in attributes)
        {
            if (!string.IsNullOrWhiteSpace(eventName) && string.Equals(item.Key, "event.name", StringComparison.Ordinal))
            {
                continue;
            }

            sanitized[SanitizeFieldName(item.Key)] = IsSensitiveKey(item.Key) ? "[REDACTED]" : SanitizeValue(item.Value);
        }

        return sanitized;
    }

    private static object? SanitizeValue(object? value)
        => value switch
        {
            null => null,
            string text => text,
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString(),
            Enum enumValue => enumValue.ToString(),
            _ => value.ToString(),
        };

    private static void AddExceptionFields(IDictionary<string, object?> fields, string prefix, Exception exception, int depth = 0)
    {
        fields[$"{prefix}_type"] = exception.GetType().FullName;
        fields[$"{prefix}_message"] = exception.Message;
        fields[$"{prefix}_hresult"] = exception.HResult;
        fields[$"{prefix}_stacktrace"] = exception.StackTrace;
        if (exception.InnerException is not null && depth < 4)
        {
            AddExceptionFields(fields, $"{prefix}_inner", exception.InnerException, depth + 1);
        }
    }

    private static string SanitizeFieldName(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "field";
        }

        var builder = new StringBuilder(key.Length);
        foreach (var character in key)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_');
        }

        while (builder.Length > 0 && builder[^1] == '_')
        {
            builder.Length--;
        }

        return builder.Length == 0 ? "field" : builder.ToString();
    }

    private static string SanitizeTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return "sunder";
        }

        var builder = new StringBuilder(tag.Length);
        foreach (var character in tag)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '_');
        }

        return builder.Length == 0 ? "sunder" : builder.ToString();
    }

    private static string FormatFieldValue(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        var text = value switch
        {
            bool boolValue => boolValue ? "true" : "false",
            IFormattable formattable when value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        return NeedsQuoting(text) ? Quote(text) : text;
    }

    private static bool NeedsQuoting(string value)
        => value.Length == 0 || value.Any(character => char.IsWhiteSpace(character) || character is '"' or '\\' or '=' or ':' or '[' or ']');

    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => character,
            });
        }

        return builder.Append('"').ToString();
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalized = key.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return SensitiveKeyFragments.Any(fragment => normalized.Contains(fragment.Replace("_", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal));
    }

    private static string ToSeverityText(PackageLogLevel level)
        => level switch
        {
            PackageLogLevel.Trace => "trace",
            PackageLogLevel.Debug => "debug",
            PackageLogLevel.Information => "info",
            PackageLogLevel.Warning => "warning",
            PackageLogLevel.Error => "error",
            PackageLogLevel.Critical => "critical",
            _ => "info",
        };
}
