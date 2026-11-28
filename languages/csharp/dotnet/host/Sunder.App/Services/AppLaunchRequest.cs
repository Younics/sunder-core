using System.Text.RegularExpressions;
using Sunder.Package.Format;
using Sunder.Sdk.Packaging;

namespace Sunder.App.Services;

public sealed record AppLaunchRequest(
    AppLaunchRequestKind Kind,
    string? PackageId = null,
    string? StackId = null,
    string? FilePath = null,
    Uri? RegistryUrl = null,
    string? ErrorMessage = null,
    string? PackageVersion = null)
{
    public bool Success => Kind != AppLaunchRequestKind.Invalid;

    public static AppLaunchRequest Invalid(string message)
        => new(AppLaunchRequestKind.Invalid, ErrorMessage: message);
}

public enum AppLaunchRequestKind
{
    None,
    Invalid,
    PackageDetails,
    PackageInstall,
    StackDetails,
    StackUse,
    StackFile,
}

public static class AppLaunchRequestParser
{
    private static readonly Regex StackIdRegex = new("^[a-z0-9]+([.-][a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static AppLaunchRequest Parse(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            var request = Parse(arg);
            if (request.Kind != AppLaunchRequestKind.None)
            {
                return request;
            }
        }

        return new AppLaunchRequest(AppLaunchRequestKind.None);
    }

    public static AppLaunchRequest Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new AppLaunchRequest(AppLaunchRequestKind.None);
        }

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && string.Equals(uri.Scheme, "sunder", StringComparison.OrdinalIgnoreCase))
        {
            return ParseSunderUri(uri);
        }

        if (trimmed.EndsWith(".sunderstack", StringComparison.OrdinalIgnoreCase))
        {
            return new AppLaunchRequest(AppLaunchRequestKind.StackFile, FilePath: Path.GetFullPath(trimmed));
        }

        return new AppLaunchRequest(AppLaunchRequestKind.None);
    }

    private static AppLaunchRequest ParseSunderUri(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        return host switch
        {
            "packages" => ParsePackageUri(uri, segments),
            "stacks" => ParseStackUri(uri, segments),
            _ => AppLaunchRequest.Invalid($"Unsupported Sunder link host '{uri.Host}'."),
        };
    }

    private static AppLaunchRequest ParsePackageUri(Uri uri, IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
        {
            return AppLaunchRequest.Invalid("Sunder package link is missing package id.");
        }

        var packageId = segments[0];
        if (!PackageId.TryParse(packageId, out _))
        {
            return AppLaunchRequest.Invalid($"Sunder package link has invalid package id '{packageId}'.");
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        RegistryUrlHelper.TryParse(query["registry"], out var registryUrl);
        if (segments.Count == 1)
        {
            return new AppLaunchRequest(AppLaunchRequestKind.PackageDetails, PackageId: packageId, RegistryUrl: registryUrl);
        }
        if (segments is [_, "install"])
        {
            if (!HasActionOrigin(uri, query, registryUrl) || query.GetValues("version") is not [var version]
                || !SemanticVersion.TryParse(version, out _))
                return AppLaunchRequest.Invalid("An install link requires an exact package version and one valid Hub URL.");
            return new(AppLaunchRequestKind.PackageInstall, PackageId: packageId, RegistryUrl: registryUrl, PackageVersion: version);
        }

        return AppLaunchRequest.Invalid($"Unsupported Sunder package link path '{uri.AbsolutePath}'.");
    }

    private static AppLaunchRequest ParseStackUri(Uri uri, IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
        {
            return AppLaunchRequest.Invalid("Sunder Stack link is missing Stack id.");
        }

        var stackId = segments[0];
        if (!StackIdRegex.IsMatch(stackId))
        {
            return AppLaunchRequest.Invalid($"Sunder Stack link has invalid Stack id '{stackId}'.");
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        RegistryUrlHelper.TryParse(query["registry"], out var registryUrl);
        if (segments.Count == 1)
        {
            return new AppLaunchRequest(AppLaunchRequestKind.StackDetails, StackId: stackId, RegistryUrl: registryUrl);
        }
        if (segments is [_, "use"])
        {
            return HasActionOrigin(uri, query, registryUrl)
                ? new(AppLaunchRequestKind.StackUse, StackId: stackId, RegistryUrl: registryUrl)
                : AppLaunchRequest.Invalid("A stack setup link requires one valid Hub URL.");
        }

        return AppLaunchRequest.Invalid($"Unsupported Sunder Stack link path '{uri.AbsolutePath}'.");
    }

    private static bool HasActionOrigin(Uri uri, System.Collections.Specialized.NameValueCollection query, Uri? origin)
        => origin is not null && query.GetValues("registry") is [var raw] && Uri.IsWellFormedUriString(raw, UriKind.Absolute)
           && uri.UserInfo.Length == 0 && uri.IsDefaultPort && uri.Fragment.Length == 0;
}
