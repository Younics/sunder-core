using System.Globalization;
using System.Text.Json;

namespace Sunder.Cli;

internal sealed record CliOptions(
    Uri? RegistryApiUrl,
    Uri? RegistryWebUrl,
    Uri? RuntimeUrl,
    TimeSpan RequestTimeout)
{
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(15);
    public static readonly Uri DefaultRuntimeUrl = new("http://127.0.0.1:5275/");
    private static readonly TimeSpan MaximumRequestTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    public Uri RequireRegistryApiUrl()
        => RegistryApiUrl ?? throw new InvalidOperationException("This command requires Registry API configuration.");

    public Uri RequireRegistryWebUrl()
        => RegistryWebUrl ?? throw new InvalidOperationException("This command requires Registry web configuration.");

    public Uri RequireRuntimeUrl()
        => RuntimeUrl ?? throw new InvalidOperationException("This command requires Runtime configuration.");

    public static CliOptions Load(
        CliOptionOverrides overrides,
        CliConfigurationRequirements requirements,
        CliAppSettings? appSettings = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        var timeout = overrides.RequestTimeout ?? DefaultRequestTimeout;
        if (requirements == CliConfigurationRequirements.None)
        {
            return new CliOptions(null, null, null, timeout);
        }

        var settings = appSettings ?? CliAppSettings.Load();
        var environment = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var needsRegistry = requirements.HasFlag(CliConfigurationRequirements.RegistryApi)
                            || requirements.HasFlag(CliConfigurationRequirements.RegistryWeb);
        var environmentApi = needsRegistry ? environment("SUNDER_REGISTRY_API_URL") : null;
        var environmentWeb = requirements.HasFlag(CliConfigurationRequirements.RegistryWeb)
            ? environment("SUNDER_REGISTRY_WEB_URL")
            : null;
        var environmentRuntime = requirements.HasFlag(CliConfigurationRequirements.Runtime)
            ? environment("SUNDER_RUNTIME_URL")
            : null;

        var apiValue = overrides.RegistryApiUrl
            ?? environmentApi
            ?? settings.RegistryApiUrl;
        var webValue = overrides.RegistryWebUrl
            ?? overrides.RegistryApiUrl
            ?? environmentWeb
            ?? environmentApi
            ?? settings.RegistryWebUrl
            ?? settings.RegistryApiUrl;
        var runtimeValue = overrides.RuntimeUrl
            ?? environmentRuntime
            ?? settings.RuntimeUrl
            ?? DefaultRuntimeUrl.AbsoluteUri;

        var registryApi = requirements.HasFlag(CliConfigurationRequirements.RegistryApi)
            ? NormalizeUrl(RequireUrl(apiValue, "Registry API", "SUNDER_REGISTRY_API_URL", "--registry-api-url"), "Registry API")
            : null;
        var registryWeb = requirements.HasFlag(CliConfigurationRequirements.RegistryWeb)
            ? NormalizeUrl(RequireUrl(webValue, "Registry web", "SUNDER_REGISTRY_WEB_URL", "--registry-web-url"), "Registry web")
            : null;
        var runtime = requirements.HasFlag(CliConfigurationRequirements.Runtime)
            ? NormalizeUrl(runtimeValue, "Runtime")
            : null;

        return new CliOptions(registryApi, registryWeb, runtime, timeout);
    }

    public static bool TryParseTimeout(string value, out TimeSpan timeout, out string? error)
    {
        timeout = default;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Option '--timeout' requires a positive duration.";
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (TryParseNumber(normalized, out var bareSeconds))
            return TryCreatePositiveTimeout(bareSeconds, TimeSpan.FromSeconds, value, out timeout, out error);
        if (normalized.EndsWith('s') && TryParseNumber(normalized[..^1], out var seconds))
            return TryCreatePositiveTimeout(seconds, TimeSpan.FromSeconds, value, out timeout, out error);
        if (normalized.EndsWith('m') && TryParseNumber(normalized[..^1], out var minutes))
            return TryCreatePositiveTimeout(minutes, TimeSpan.FromMinutes, value, out timeout, out error);
        if (TimeSpan.TryParse(normalized, CultureInfo.InvariantCulture, out timeout) && timeout > TimeSpan.Zero)
        {
            if (timeout <= MaximumRequestTimeout) return true;
            error = $"Option '--timeout' value '{value}' is too large.";
            timeout = default;
            return false;
        }

        timeout = default;
        error = "Option '--timeout' must be a positive duration like 15m, 900s, 900, or 00:15:00.";
        return false;
    }

    private static bool TryParseNumber(string value, out double result)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
           && double.IsFinite(result);

    private static bool TryCreatePositiveTimeout(
        double value,
        Func<double, TimeSpan> factory,
        string originalValue,
        out TimeSpan timeout,
        out string? error)
    {
        timeout = default;
        if (value <= 0)
        {
            error = "Option '--timeout' must be a positive duration like 15m, 900s, 900, or 00:15:00.";
            return false;
        }

        try
        {
            timeout = factory(value);
            if (timeout <= TimeSpan.Zero)
            {
                timeout = default;
                error = "Option '--timeout' must be a positive duration like 15m, 900s, 900, or 00:15:00.";
                return false;
            }
            if (timeout > MaximumRequestTimeout)
            {
                timeout = default;
                error = $"Option '--timeout' value '{originalValue}' is too large.";
                return false;
            }
            error = null;
            return true;
        }
        catch (OverflowException)
        {
            error = $"Option '--timeout' value '{originalValue}' is too large.";
            return false;
        }
    }

    private static string RequireUrl(string? value, string label, string environmentVariable, string option)
    {
        if (!string.IsNullOrWhiteSpace(value)) return value;
        throw new CliConfigurationException(
            $"{label} URL is not configured. Set it in appsettings.json, {environmentVariable}, or {option}.");
    }

    private static Uri NormalizeUrl(string value, string label)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new CliConfigurationException($"Invalid {label} URL.");
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new CliConfigurationException($"Invalid {label} URL: only HTTP and HTTPS URLs are supported.");
        if (!string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new CliConfigurationException(
                $"Invalid {label} URL: user information, query strings, and fragments are not allowed.");
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !uri.IsLoopback)
            throw new CliConfigurationException($"Invalid {label} URL: HTTPS is required unless the host is loopback.");

        var builder = new UriBuilder(uri);
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal)) builder.Path += "/";
        return builder.Uri;
    }
}

internal sealed record CliAppSettings(string? RegistryApiUrl, string? RegistryWebUrl, string? RuntimeUrl)
{
    public static CliAppSettings Load(string? baseDirectory = null, string? environmentName = null)
    {
        var directory = baseDirectory ?? AppContext.BaseDirectory;
        var result = Merge(new CliAppSettings(null, null, null), LoadFile(directory, "appsettings.json"));

        var environment = environmentName ?? ResolveEnvironmentName();
        if (!string.IsNullOrWhiteSpace(environment))
        {
            if (environment.Length > 64 || environment.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
                throw new CliConfigurationException("The selected Sunder environment name is invalid.");
            result = Merge(result, LoadFile(directory, $"appsettings.{environment}.json"));
        }

        return result;
    }

    private static CliAppSettings Merge(CliAppSettings current, CliAppSettings next)
        => new(
            next.RegistryApiUrl ?? current.RegistryApiUrl,
            next.RegistryWebUrl ?? current.RegistryWebUrl,
            next.RuntimeUrl ?? current.RuntimeUrl);

    private static CliAppSettings LoadFile(string baseDirectory, string fileName)
    {
        var path = Path.Combine(baseDirectory, fileName);
        if (!File.Exists(path)) return new CliAppSettings(null, null, null);

        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new CliConfigurationException($"CLI configuration file '{fileName}' must contain a JSON object.");
            return new CliAppSettings(
                GetString(document.RootElement, fileName, "Registry", "ApiUrl"),
                GetString(document.RootElement, fileName, "Registry", "WebUrl"),
                GetString(document.RootElement, fileName, "Runtime", "Url"));
        }
        catch (CliConfigurationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new CliConfigurationException($"CLI configuration file '{fileName}' is not valid JSON: {exception.Message}", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CliConfigurationException($"CLI configuration file '{fileName}' could not be read: {exception.Message}", exception);
        }
    }

    private static string? GetString(JsonElement root, string fileName, string sectionName, string propertyName)
    {
        if (!root.TryGetProperty(sectionName, out var section)) return null;
        if (section.ValueKind != JsonValueKind.Object)
            throw new CliConfigurationException($"CLI configuration section '{sectionName}' in '{fileName}' must be a JSON object.");
        if (!section.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new CliConfigurationException(
                $"CLI configuration value '{sectionName}:{propertyName}' in '{fileName}' must be a string.");
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
