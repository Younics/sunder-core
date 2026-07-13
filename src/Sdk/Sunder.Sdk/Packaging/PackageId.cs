using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Packaging;

/// <summary>Represents a canonical lowercase dot-separated ASCII package identifier.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackagingV1)]
public readonly record struct PackageId
{
    /// <summary>Maximum supported package identifier length.</summary>
    public const int MaximumLength = 128;

    private readonly string? _value;

    private PackageId(string value) => _value = value;

    /// <summary>Parses a canonical package identifier.</summary>
    /// <exception cref="FormatException"><paramref name="value"/> is not a canonical package identifier.</exception>
    public static PackageId Parse(string value)
        => TryParse(value, out var packageId)
            ? packageId
            : throw new FormatException($"'{value}' is not a lowercase dot-separated ASCII package id of at most {MaximumLength} characters.");

    /// <summary>Attempts to parse a canonical package identifier.</summary>
    public static bool TryParse(string? value, out PackageId packageId)
    {
        packageId = default;
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength)
        {
            return false;
        }

        var segments = value.Split('.');
        if (segments.Any(static segment => segment.Length == 0
                                           || segment.Any(static character => character is not (>= 'a' and <= 'z')
                                                                                           and not (>= '0' and <= '9'))))
        {
            return false;
        }

        packageId = new PackageId(value);
        return true;
    }

    /// <summary>Returns the canonical identifier.</summary>
    public override string ToString() => _value ?? string.Empty;
}
