namespace Sunder.Cli;

internal static class CliHttpUrl
{
    public static Uri Require(Uri uri, string label)
    {
        if (!uri.IsAbsoluteUri
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"{label} URL '{uri}' must use HTTP or HTTPS.");
        }

        return uri;
    }
}
