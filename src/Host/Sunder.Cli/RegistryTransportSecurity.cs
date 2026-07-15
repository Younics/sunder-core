namespace Sunder.Cli;

internal static class RegistryTransportSecurity
{
    public static Uri CreateUri(Uri registryApiUrl, string path)
    {
        var uri = Uri.TryCreate(path, UriKind.Absolute, out var absolute)
                  && (string.Equals(absolute.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                      || string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            ? absolute
            : new Uri(registryApiUrl, path);
        return CliHttpUrl.Require(uri, "Registry request");
    }

    public static void RequireSameOrigin(Uri registryApiUrl, Uri? uri)
    {
        if (uri is null
            || !string.Equals(uri.Scheme, registryApiUrl.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, registryApiUrl.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != registryApiUrl.Port
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("Registry Stack artifact URL must remain on the trusted Registry origin.");
        }
    }
}
