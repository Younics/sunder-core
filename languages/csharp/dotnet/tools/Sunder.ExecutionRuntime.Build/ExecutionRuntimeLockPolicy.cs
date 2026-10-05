using System.Text.RegularExpressions;
using Sunder.Package.Format;

namespace Sunder.ExecutionRuntime.Build;

internal static class ExecutionRuntimeLockPolicy
{
    public const string SchemaUri = "https://sunder.dev/eng/execution-runtimes/lock-v1.schema.json";
    public const string ProductionKind = "production";
    public const string TestFixtureKind = "test-fixture";
    public const string FixtureArchiveFormat = "fixture-json";
    public const string ZipArchiveFormat = "zip";
    public const string TarGzipArchiveFormat = "tar.gz";
    public const string RecipeName = "repackage-upstream-tree-v1";
    public const string PayloadRoot = "payload/runtime";
    public const string RequiredTrustCheck = "required";
    public const string NotApplicableTrustCheck = "not-applicable";
    public const string LongTermSupportLine = "lts";
    public const string SupportedDefaultStatus = "supported";
    public const int MaxLockBytes = 4 * 1024 * 1024;
    public const long MaxSafeJsonInteger = 9_007_199_254_740_991;

    private static readonly Regex StableSemanticVersion = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static readonly IReadOnlyList<string> Rids =
        SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers;

    public static readonly IReadOnlyList<RuntimeFamily> RuntimeFamilies = Array.AsReadOnly(
        [
            new RuntimeFamily(
                SunderExecutionRuntimeFormat.DotNetRuntimeId,
                10,
                "dotnet",
                "microsoft-dotnet",
                "dotnet-worker-v1",
                SunderExecutionRuntimeFormat.DotNetModuleAbi,
                "dotnet-sdk",
                "LICENSE.txt"),
            new RuntimeFamily(
                SunderExecutionRuntimeFormat.NodeRuntimeId,
                24,
                "node",
                "openjs-node",
                "node-worker-v1",
                SunderExecutionRuntimeFormat.NodeModuleAbi,
                "node-build",
                "LICENSE"),
        ]);

    public static bool TryParseStableVersion(string? value, out int major)
    {
        major = -1;
        if (value is null)
        {
            return false;
        }

        var match = StableSemanticVersion.Match(value);
        return match.Success
               && int.TryParse(match.Groups[1].Value, out major)
               && int.TryParse(match.Groups[2].Value, out _)
               && int.TryParse(match.Groups[3].Value, out _);
    }

    public static bool IsLowercaseSha256(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsBoundedIdentifier(string? value, int maxLength = 128)
        => !string.IsNullOrEmpty(value)
           && value.Length <= maxLength
           && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
           && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
           && !value.Contains("..", StringComparison.Ordinal)
           && value.Split('.').All(static segment => segment.Length > 0
                                                       && segment[0] != '-'
                                                       && segment[^1] != '-')
           && value.All(static character => character is >= 'a' and <= 'z'
                                                   or >= '0' and <= '9'
                                                   or '.'
                                                   or '-');

    public static bool TryNormalizeRelativePath(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(value)
            || value.Length > SunderExecutionRuntimeFormat.MaxArchivePathLength
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Any(static character => character is < '!' or > '~'))
        {
            return false;
        }

        var segments = value.Split('/');
        if (segments.Length > SunderExecutionRuntimeFormat.MaxArchivePathDepth
            || segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
        {
            return false;
        }

        normalized = string.Join('/', segments);
        return true;
    }

    public static bool TryNormalizeSourceRoot(string? value, out string normalized)
    {
        if (string.Equals(value, ".", StringComparison.Ordinal))
        {
            normalized = ".";
            return true;
        }
        return TryNormalizeRelativePath(value, out normalized);
    }

    public static bool TryGetHttpsUri(string? value, out Uri? uri)
    {
        uri = null;
        return !string.IsNullOrEmpty(value)
               && value.Length <= 2048
               && !value.Contains('\\', StringComparison.Ordinal)
               && value.All(static character => character is >= '!' and <= '~')
               && Uri.TryCreate(value, UriKind.Absolute, out uri)
               && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
               && string.IsNullOrEmpty(uri.UserInfo)
               && string.IsNullOrEmpty(uri.Query)
               && string.IsNullOrEmpty(uri.Fragment);
    }

    public static bool IsReservedHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
           || string.Equals(host, "invalid", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)
           || string.Equals(host, "test", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".test", StringComparison.OrdinalIgnoreCase)
           || string.Equals(host, "example", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".example", StringComparison.OrdinalIgnoreCase)
           || System.Net.IPAddress.TryParse(host, out _);

    public static RuntimeFamily? FindFamily(string? id)
        => RuntimeFamilies.SingleOrDefault(family => string.Equals(family.Id, id, StringComparison.Ordinal));

    public sealed record RuntimeFamily(
        string Id,
        int Major,
        string UpstreamName,
        string Publisher,
        string ProfileName,
        string ModuleAbi,
        string ToolchainName,
        string LicensePath);
}
