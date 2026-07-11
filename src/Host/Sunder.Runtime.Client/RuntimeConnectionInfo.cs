namespace Sunder.Runtime.Client;

public sealed record RuntimeConnectionInfo(Uri RuntimeUrl, string BearerToken)
{
    public bool Matches(Uri runtimeUrl)
        => Normalize(RuntimeUrl) == Normalize(runtimeUrl);

    public bool CanSendTo(Uri requestUri)
    {
        var runtimeUrl = Normalize(RuntimeUrl);
        return string.Equals(runtimeUrl.Scheme, requestUri.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(runtimeUrl.Host, requestUri.Host, StringComparison.OrdinalIgnoreCase)
               && runtimeUrl.Port == requestUri.Port
               && requestUri.AbsolutePath.StartsWith(runtimeUrl.AbsolutePath, StringComparison.Ordinal);
    }

    public static Uri Normalize(Uri runtimeUrl)
    {
        ArgumentNullException.ThrowIfNull(runtimeUrl);
        if (!runtimeUrl.IsAbsoluteUri
            || !string.Equals(runtimeUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(runtimeUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(runtimeUrl.UserInfo))
        {
            throw new ArgumentException("Runtime URL must be an absolute HTTP or HTTPS URL.", nameof(runtimeUrl));
        }

        var builder = new UriBuilder(runtimeUrl)
        {
            Fragment = string.Empty,
            Query = string.Empty,
        };
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }
}
