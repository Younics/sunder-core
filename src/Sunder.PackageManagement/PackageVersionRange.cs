namespace Sunder.PackageManagement;

public static class PackageVersionRange
{
    public static bool IsSatisfiedBy(string version, string range)
    {
        if (!TryParseVersion(version, out var parsedVersion) || string.IsNullOrWhiteSpace(range))
        {
            return false;
        }

        var tokens = range.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        foreach (var token in tokens)
        {
            if (!IsTokenSatisfiedBy(parsedVersion, token))
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryCompare(string left, string right, out int comparison)
    {
        comparison = 0;
        if (!TryParseVersion(left, out var leftVersion) || !TryParseVersion(right, out var rightVersion))
        {
            return false;
        }

        comparison = leftVersion.CompareTo(rightVersion);
        return true;
    }

    private static bool IsTokenSatisfiedBy(Version version, string token)
    {
        var operators = new[] { ">=", "<=", ">", "<", "=" };
        foreach (var comparisonOperator in operators)
        {
            if (!token.StartsWith(comparisonOperator, StringComparison.Ordinal))
            {
                continue;
            }

            return TryParseVersion(token[comparisonOperator.Length..], out var requiredVersion)
                && Compare(version, requiredVersion, comparisonOperator);
        }

        return TryParseVersion(token, out var exactVersion)
            && version.CompareTo(exactVersion) == 0;
    }

    private static bool Compare(Version version, Version requiredVersion, string comparisonOperator)
        => comparisonOperator switch
        {
            ">=" => version.CompareTo(requiredVersion) >= 0,
            "<=" => version.CompareTo(requiredVersion) <= 0,
            ">" => version.CompareTo(requiredVersion) > 0,
            "<" => version.CompareTo(requiredVersion) < 0,
            "=" => version.CompareTo(requiredVersion) == 0,
            _ => false,
        };

    private static bool TryParseVersion(string value, out Version version)
    {
        var normalized = value.Trim();
        var suffixIndex = normalized.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        return Version.TryParse(normalized, out version!);
    }
}
