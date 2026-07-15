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
            || parsed.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment)
            || parsed.Scheme == Uri.UriSchemeHttp && !parsed.IsLoopback)
        {
            return false;
        }

        registryUrl = NormalizeValidated(parsed);
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
        if (!TryParse(registryUrl.AbsoluteUri, out var normalized) || normalized is null)
        {
            throw new ArgumentException($"Invalid registry URL '{registryUrl}'.", nameof(registryUrl));
        }

        return normalized;
    }

    private static Uri NormalizeValidated(Uri registryUrl)
    {
        var builder = new UriBuilder(registryUrl)
        {
            Host = registryUrl.IdnHost.ToLowerInvariant(),
            Query = string.Empty,
            Fragment = string.Empty,
        };
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private static Uri ResolveDefaultRegistryUrl()
    {
        var configuredUrl = Environment.GetEnvironmentVariable("SUNDER_REGISTRY_API_URL")
            ?? SunderAppSettings.Load().RegistryApiUrl;
        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            throw new InvalidOperationException("Registry API URL is missing. Configure Registry:ApiUrl in appsettings.json or set SUNDER_REGISTRY_API_URL.");
        }

        return Normalize(configuredUrl);
    }
}
