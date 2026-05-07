namespace Sunder.App.Services;

public static class RegistryUrlHelper
{
    public static Uri DefaultRegistryUrl { get; } = ResolveDefaultRegistryUrl();

    public static bool TryParse(string? value, out Uri? registryUrl)
    {
        registryUrl = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https"))
        {
            return false;
        }

        registryUrl = Normalize(parsed);
        return true;
    }

    public static Uri Normalize(string value)
    {
        if (!TryParse(value, out var registryUrl) || registryUrl is null)
        {
            throw new ArgumentException($"Invalid registry URL '{value}'.");
        }

        return registryUrl;
    }

    public static Uri Normalize(Uri registryUrl)
    {
        var builder = new UriBuilder(registryUrl);
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private static Uri ResolveDefaultRegistryUrl()
    {
        var configuredUrl = Environment.GetEnvironmentVariable("SUNDER_REGISTRY_API_URL")
            ?? Environment.GetEnvironmentVariable("SUNDER_REGISTRY_URL")
            ?? SunderAppSettings.Load().RegistryApiUrl;
        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            throw new InvalidOperationException("Registry API URL is missing. Configure Registry:ApiUrl in appsettings.json or set SUNDER_REGISTRY_API_URL.");
        }

        return Normalize(configuredUrl);
    }
}
