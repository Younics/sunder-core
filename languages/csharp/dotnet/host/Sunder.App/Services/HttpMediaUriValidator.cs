using Sunder.Runtime.Client;

namespace Sunder.App.Services;

internal static class HttpMediaUriValidator
{
    public static bool IsValid(Uri? uri)
        => uri is { IsAbsoluteUri: true }
           && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
           && !string.IsNullOrWhiteSpace(uri.Host)
           && string.IsNullOrEmpty(uri.UserInfo);

    public static bool IsRuntimeAsset(Uri? uri, RuntimeConnectionInfo? connection)
        => IsValid(uri)
           && connection is not null
           && connection.CanSendTo(uri!);

    public static bool HasSameOrigin(Uri expected, Uri? actual)
        => IsValid(expected)
           && IsValid(actual)
           && string.Equals(expected.Scheme, actual!.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(expected.Host, actual.Host, StringComparison.OrdinalIgnoreCase)
           && expected.Port == actual.Port;
}
