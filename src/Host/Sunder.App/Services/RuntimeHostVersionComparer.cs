using System.Globalization;

namespace Sunder.App.Services;

internal static class RuntimeHostVersionComparer
{
    public static bool TryCompare(string left, string right, out int comparison)
    {
        comparison = 0;
        if (!TryParse(left, out var leftVersion) || !TryParse(right, out var rightVersion))
        {
            return false;
        }

        comparison = leftVersion.Major.CompareTo(rightVersion.Major);
        if (comparison != 0)
        {
            return true;
        }

        comparison = leftVersion.Minor.CompareTo(rightVersion.Minor);
        if (comparison != 0)
        {
            return true;
        }

        comparison = leftVersion.Patch.CompareTo(rightVersion.Patch);
        if (comparison != 0)
        {
            return true;
        }

        comparison = ComparePrerelease(leftVersion.Prerelease, rightVersion.Prerelease);
        return true;
    }

    private static bool TryParse(string value, out ParsedRuntimeHostVersion version)
    {
        version = default;
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        var buildMetadataIndex = normalized.IndexOf('+', StringComparison.Ordinal);
        if (buildMetadataIndex >= 0)
        {
            normalized = normalized[..buildMetadataIndex];
        }

        string? prerelease = null;
        var prereleaseIndex = normalized.IndexOf('-', StringComparison.Ordinal);
        if (prereleaseIndex >= 0)
        {
            prerelease = normalized[(prereleaseIndex + 1)..];
            normalized = normalized[..prereleaseIndex];
            if (prerelease.Length == 0)
            {
                return false;
            }
        }

        var parts = normalized.Split('.', StringSplitOptions.None);
        if (parts.Length < 3)
        {
            return false;
        }

        if (!TryParseNonNegativeInt(parts[0], out var major)
            || !TryParseNonNegativeInt(parts[1], out var minor)
            || !TryParseNonNegativeInt(parts[2], out var patch))
        {
            return false;
        }

        version = new ParsedRuntimeHostVersion(major, minor, patch, prerelease);
        return true;
    }

    private static int ComparePrerelease(string? left, string? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var leftIdentifiers = left.Split('.', StringSplitOptions.None);
        var rightIdentifiers = right.Split('.', StringSplitOptions.None);
        var identifierCount = Math.Min(leftIdentifiers.Length, rightIdentifiers.Length);
        for (var index = 0; index < identifierCount; index++)
        {
            var leftIdentifier = leftIdentifiers[index];
            var rightIdentifier = rightIdentifiers[index];
            var leftIsNumeric = TryParseNonNegativeInt(leftIdentifier, out var leftNumber);
            var rightIsNumeric = TryParseNonNegativeInt(rightIdentifier, out var rightNumber);

            if (leftIsNumeric && rightIsNumeric)
            {
                var numberComparison = leftNumber.CompareTo(rightNumber);
                if (numberComparison != 0)
                {
                    return numberComparison;
                }

                continue;
            }

            if (leftIsNumeric)
            {
                return -1;
            }

            if (rightIsNumeric)
            {
                return 1;
            }

            var textComparison = string.CompareOrdinal(leftIdentifier, rightIdentifier);
            if (textComparison != 0)
            {
                return textComparison;
            }
        }

        return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
    }

    private static bool TryParseNonNegativeInt(string value, out int result)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
           && result >= 0;

    private readonly record struct ParsedRuntimeHostVersion(
        int Major,
        int Minor,
        int Patch,
        string? Prerelease);
}
