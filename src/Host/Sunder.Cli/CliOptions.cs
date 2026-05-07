using System.Text.Json;

namespace Sunder.Cli;

internal sealed record CliOptions(Uri RegistryApiUrl, Uri RegistryWebUrl, Uri RuntimeUrl)
{
    public static CliOptions Parse(List<string> args)
    {
        var settings = CliAppSettings.Load();
        var legacyRegistryUrl = Environment.GetEnvironmentVariable("SUNDER_REGISTRY_URL");
        var registryApiUrl = Environment.GetEnvironmentVariable("SUNDER_REGISTRY_API_URL")
            ?? legacyRegistryUrl
            ?? settings.RegistryApiUrl;
        var registryWebUrl = Environment.GetEnvironmentVariable("SUNDER_REGISTRY_WEB_URL")
            ?? legacyRegistryUrl
            ?? settings.RegistryWebUrl
            ?? registryApiUrl;
        var runtimeUrl = Environment.GetEnvironmentVariable("SUNDER_RUNTIME_URL")
            ?? settings.RuntimeUrl
            ?? "http://127.0.0.1:5275/";

        var registryUrlAlias = CommandLine.ConsumeOption(args, "--registry-url");
        registryApiUrl = CommandLine.ConsumeOption(args, "--registry-api-url") ?? registryUrlAlias ?? registryApiUrl;
        registryWebUrl = CommandLine.ConsumeOption(args, "--registry-web-url") ?? registryUrlAlias ?? registryWebUrl;
        runtimeUrl = CommandLine.ConsumeOption(args, "--runtime-url") ?? runtimeUrl;

        return new CliOptions(
            NormalizeUrl(RequireUrl(registryApiUrl, "Registry:ApiUrl"), "registry API"),
            NormalizeUrl(RequireUrl(registryWebUrl, "Registry:WebUrl"), "registry web"),
            NormalizeUrl(runtimeUrl, "runtime"));
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
