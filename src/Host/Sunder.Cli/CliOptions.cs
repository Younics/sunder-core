using System.Globalization;
using System.Text.Json;

namespace Sunder.Cli;

internal sealed record CliOptions(Uri RegistryApiUrl, Uri RegistryWebUrl, Uri RuntimeUrl, TimeSpan RequestTimeout, bool Json)
{
    public static readonly TimeSpan DefaultRegistryTimeout = TimeSpan.FromMinutes(15);

    public static CliOptions Parse(List<string> args)
    {
        var settings = CliAppSettings.Load();
        var registryApiUrl = Environment.GetEnvironmentVariable("SUNDER_REGISTRY_API_URL")
            ?? settings.RegistryApiUrl;
        var registryWebUrl = Environment.GetEnvironmentVariable("SUNDER_REGISTRY_WEB_URL")
            ?? settings.RegistryWebUrl
            ?? registryApiUrl;
        var runtimeUrl = Environment.GetEnvironmentVariable("SUNDER_RUNTIME_URL")
            ?? settings.RuntimeUrl
            ?? "http://127.0.0.1:5275/";

        registryApiUrl = ConsumeOption(args, "--registry-api-url") ?? registryApiUrl;
        registryWebUrl = ConsumeOption(args, "--registry-web-url") ?? registryWebUrl;
        runtimeUrl = ConsumeOption(args, "--runtime-url") ?? runtimeUrl;
        var timeout = ParseTimeout(ConsumeOption(args, "--timeout"));
        var json = ConsumeFlag(args, "--json");

        return new CliOptions(
            NormalizeUrl(RequireUrl(registryApiUrl, "Registry:ApiUrl"), "registry API"),
            NormalizeUrl(RequireUrl(registryWebUrl, "Registry:WebUrl"), "registry web"),
            NormalizeUrl(runtimeUrl, "runtime"),
            timeout,
            json);
    }

    private static bool ConsumeFlag(List<string> args, string name)
    {
        var indexes = FindIndexes(args, name);
        if (indexes.Count > 1) throw new CliUsageException($"Option '{name}' may only be specified once.");
        if (indexes.Count == 0) return false;
        args.RemoveAt(indexes[0]);
        return true;
    }

    private static string? ConsumeOption(List<string> args, string name)
    {
        var indexes = FindIndexes(args, name);
        if (indexes.Count > 1) throw new CliUsageException($"Option '{name}' may only be specified once.");
        if (indexes.Count == 0) return null;
        var index = indexes[0];
        if (index + 1 >= args.Count || args[index + 1].StartsWith("-", StringComparison.Ordinal))
            throw new CliUsageException($"Option '{name}' requires a value.");
        var value = args[index + 1];
        args.RemoveRange(index, 2);
        return value;
    }

    private static List<int> FindIndexes(List<string> args, string name)
        => args.Select((value, index) => (value, index))
            .Where(item => string.Equals(item.value, name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToList();

    private static TimeSpan ParseTimeout(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultRegistryTimeout;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (TryParseNumber(normalized, out var bareSeconds))
        {
            return CreatePositiveTimeout(bareSeconds, TimeSpan.FromSeconds, value);
        }

        if (normalized.EndsWith('s')
            && TryParseNumber(normalized[..^1], out var seconds))
        {
            return CreatePositiveTimeout(seconds, TimeSpan.FromSeconds, value);
        }

        if (normalized.EndsWith('m')
            && TryParseNumber(normalized[..^1], out var minutes))
        {
            return CreatePositiveTimeout(minutes, TimeSpan.FromMinutes, value);
        }

        if (TimeSpan.TryParse(normalized, CultureInfo.InvariantCulture, out var timeSpan)
            && timeSpan > TimeSpan.Zero)
        {
            return timeSpan;
        }

        throw new ArgumentException("Option '--timeout' must be a positive duration like 15m, 900s, 900, or 00:15:00.");
    }

    private static bool TryParseNumber(string value, out double result)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
           && double.IsFinite(result);

    private static TimeSpan CreatePositiveTimeout(
        double value,
        Func<double, TimeSpan> factory,
        string originalValue)
    {
        if (value <= 0)
        {
            throw new ArgumentException("Option '--timeout' must be a positive duration like 15m, 900s, 900, or 00:15:00.");
        }

        try
        {
            return factory(value);
        }
        catch (OverflowException)
        {
            throw new ArgumentException($"Option '--timeout' value '{originalValue}' is too large.");
        }
    }

    private static string RequireUrl(string? value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Registry URL setting '{settingName}' is missing. Configure appsettings.json, SUNDER_REGISTRY_API_URL, or --registry-api-url.");
        }

        return value;
    }

    private static Uri NormalizeUrl(string value, string label)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid {label} URL '{value}'.");
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid {label} URL '{value}': only HTTP and HTTPS URLs are supported.");
        }

        var builder = new UriBuilder(uri);
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }
}

internal sealed record CliAppSettings(string? RegistryApiUrl, string? RegistryWebUrl, string? RuntimeUrl)
{
    public static CliAppSettings Load()
    {
        var result = new CliAppSettings(null, null, null);
        result = Merge(result, LoadFile("appsettings.json"));

        var environment = ResolveEnvironmentName();
        if (!string.IsNullOrWhiteSpace(environment))
        {
            result = Merge(result, LoadFile($"appsettings.{environment}.json"));
        }

        return result;
    }

    private static CliAppSettings Merge(CliAppSettings current, CliAppSettings next)
        => new(
            next.RegistryApiUrl ?? current.RegistryApiUrl,
            next.RegistryWebUrl ?? current.RegistryWebUrl,
            next.RuntimeUrl ?? current.RuntimeUrl);

    private static CliAppSettings LoadFile(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
        {
            return new CliAppSettings(null, null, null);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        return new CliAppSettings(
            GetString(root, "Registry", "ApiUrl"),
            GetString(root, "Registry", "WebUrl"),
            GetString(root, "Runtime", "Url"));
    }

    private static string? GetString(JsonElement root, string sectionName, string propertyName)
    {
        if (!root.TryGetProperty(sectionName, out var section)
            || !section.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? ResolveEnvironmentName()
        => Environment.GetEnvironmentVariable("SUNDER_ENVIRONMENT")
           ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
           ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
#if DEBUG
           ?? "Development"
#endif
        ;
}
