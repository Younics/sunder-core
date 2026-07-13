using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Packaging;

/// <summary>Represents a strict Semantic Versioning 2.0.0 version.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackagingV1)]
public readonly struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    /// <summary>Maximum supported semantic-version string length.</summary>
    public const int MaximumLength = 256;

    private readonly string? _major;
    private readonly string? _minor;
    private readonly string? _patch;

    private SemanticVersion(string major, string minor, string patch, string? prerelease, string? buildMetadata)
    {
        _major = major;
        _minor = minor;
        _patch = patch;
        Prerelease = prerelease;
        BuildMetadata = buildMetadata;
    }

    /// <summary>Gets the major numeric identifier.</summary>
    public string Major => _major ?? throw new InvalidOperationException("The semantic version is uninitialized.");

    /// <summary>Gets the minor numeric identifier.</summary>
    public string Minor => _minor ?? throw new InvalidOperationException("The semantic version is uninitialized.");

    /// <summary>Gets the patch numeric identifier.</summary>
    public string Patch => _patch ?? throw new InvalidOperationException("The semantic version is uninitialized.");

    /// <summary>Gets the prerelease identifiers, when present.</summary>
    public string? Prerelease { get; }

    /// <summary>Gets the build metadata identifiers, when present.</summary>
    public string? BuildMetadata { get; }

    /// <summary>Parses a strict Semantic Versioning 2.0.0 version.</summary>
    /// <exception cref="FormatException"><paramref name="value"/> is invalid.</exception>
    public static SemanticVersion Parse(string value)
        => TryParse(value, out var version)
            ? version
            : throw new FormatException($"'{value}' is not a strict SemVer 2.0 version of at most {MaximumLength} characters.");

    /// <summary>Attempts to parse a strict Semantic Versioning 2.0.0 version.</summary>
    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var buildSeparator = value.IndexOf('+');
        if (buildSeparator >= 0 && value.IndexOf('+', buildSeparator + 1) >= 0)
        {
            return false;
        }

        var buildMetadata = buildSeparator < 0 ? null : value[(buildSeparator + 1)..];
        var precedencePart = buildSeparator < 0 ? value : value[..buildSeparator];
        if (buildMetadata is not null && !AreIdentifiersValid(buildMetadata, numericLeadingZerosAllowed: true))
        {
            return false;
        }

        var prereleaseSeparator = precedencePart.IndexOf('-');
        var prerelease = prereleaseSeparator < 0 ? null : precedencePart[(prereleaseSeparator + 1)..];
        var core = prereleaseSeparator < 0 ? precedencePart : precedencePart[..prereleaseSeparator];
        if (prerelease is not null && !AreIdentifiersValid(prerelease, numericLeadingZerosAllowed: false))
        {
            return false;
        }

        var components = core.Split('.');
        if (components.Length != 3 || components.Any(static component => !IsCoreNumberValid(component)))
        {
            return false;
        }

        version = new SemanticVersion(components[0], components[1], components[2], prerelease, buildMetadata);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(SemanticVersion other)
    {
        EnsureInitialized();
        other.EnsureInitialized();

        var comparison = CompareNumericIdentifier(Major, other.Major);
        if (comparison != 0) return comparison;
        comparison = CompareNumericIdentifier(Minor, other.Minor);
        if (comparison != 0) return comparison;
        comparison = CompareNumericIdentifier(Patch, other.Patch);
        if (comparison != 0) return comparison;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;

        var leftIdentifiers = Prerelease.Split('.');
        var rightIdentifiers = other.Prerelease.Split('.');
        for (var index = 0; index < Math.Min(leftIdentifiers.Length, rightIdentifiers.Length); index++)
        {
            var left = leftIdentifiers[index];
            var right = rightIdentifiers[index];
            var leftNumeric = IsAsciiDigits(left);
            var rightNumeric = IsAsciiDigits(right);
            comparison = leftNumeric && rightNumeric
                ? CompareNumericIdentifier(left, right)
                : leftNumeric != rightNumeric
                    ? leftNumeric ? -1 : 1
                    : string.Compare(left, right, StringComparison.Ordinal);
            if (comparison != 0) return comparison;
        }

        return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
    }

    /// <summary>Returns whether this version has the same precedence as <paramref name="other"/>, ignoring build metadata.</summary>
    public bool HasSamePrecedence(SemanticVersion other) => CompareTo(other) == 0;

    /// <summary>Returns the next major version with minor and patch reset to zero.</summary>
    public SemanticVersion NextMajor() => new(IncrementDecimal(Major), "0", "0", null, null);

    /// <inheritdoc />
    public bool Equals(SemanticVersion other)
        => string.Equals(_major, other._major, StringComparison.Ordinal)
           && string.Equals(_minor, other._minor, StringComparison.Ordinal)
           && string.Equals(_patch, other._patch, StringComparison.Ordinal)
           && string.Equals(Prerelease, other.Prerelease, StringComparison.Ordinal)
           && string.Equals(BuildMetadata, other.BuildMetadata, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_major, _minor, _patch, Prerelease, BuildMetadata);

    /// <inheritdoc />
    public override string ToString()
    {
        EnsureInitialized();
        return $"{Major}.{Minor}.{Patch}"
               + (Prerelease is null ? string.Empty : $"-{Prerelease}")
               + (BuildMetadata is null ? string.Empty : $"+{BuildMetadata}");
    }

    /// <summary>Returns whether <paramref name="left"/> has lower precedence than <paramref name="right"/>.</summary>
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    /// <summary>Returns whether <paramref name="left"/> has lower or equal precedence to <paramref name="right"/>.</summary>
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    /// <summary>Returns whether <paramref name="left"/> has higher precedence than <paramref name="right"/>.</summary>
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    /// <summary>Returns whether <paramref name="left"/> has higher or equal precedence to <paramref name="right"/>.</summary>
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    /// <summary>Returns whether the versions are equal, including build metadata.</summary>
    public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);

    /// <summary>Returns whether the versions differ.</summary>
    public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);

    private static bool IsCoreNumberValid(string value)
        => IsAsciiDigits(value) && (value.Length == 1 || value[0] != '0');

    private static bool AreIdentifiersValid(string value, bool numericLeadingZerosAllowed)
    {
        if (value.Length == 0) return false;
        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0
                || identifier.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            {
                return false;
            }

            if (!numericLeadingZerosAllowed && IsAsciiDigits(identifier) && identifier.Length > 1 && identifier[0] == '0')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiDigits(string value)
        => value.Length > 0 && value.All(static character => character is >= '0' and <= '9');

    private static int CompareNumericIdentifier(string left, string right)
    {
        var lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0 ? lengthComparison : string.Compare(left, right, StringComparison.Ordinal);
    }

    private static string IncrementDecimal(string value)
    {
        var characters = value.ToCharArray();
        for (var index = characters.Length - 1; index >= 0; index--)
        {
            if (characters[index] != '9')
            {
                characters[index]++;
                return new string(characters);
            }

            characters[index] = '0';
        }

        return "1" + new string(characters);
    }

    private void EnsureInitialized()
    {
        if (_major is null) throw new InvalidOperationException("The semantic version is uninitialized.");
    }
}
