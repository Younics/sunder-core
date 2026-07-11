namespace Sunder.Package.Format;

/// <summary>
/// A deliberately small range grammar: one exact SemVer, or one or more
/// space-conjoined comparisons using &lt;, &lt;=, &gt;, &gt;=, or =.
/// </summary>
public readonly struct PackageVersionRange : IEquatable<PackageVersionRange>
{
    private readonly Constraint[]? _constraints;

    private PackageVersionRange(Constraint[] constraints) => _constraints = constraints;

    public static PackageVersionRange Parse(string value)
        => TryParse(value, out var range)
            ? range
            : throw new FormatException($"'{value}' is not a supported package version range.");

    public static bool TryParse(string? value, out PackageVersionRange range)
    {
        range = default;
        if (string.IsNullOrEmpty(value)
            || value != value.Trim()
            || value.Any(static character => char.IsWhiteSpace(character) && character != ' '))
        {
            return false;
        }

        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var constraints = new Constraint[tokens.Length];
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            var comparisonOperator = ReadOperator(token, out var versionText);
            if (comparisonOperator == ComparisonOperator.Exact && tokens.Length != 1)
            {
                return false;
            }

            if (!SemanticVersion.TryParse(versionText, out var version))
            {
                return false;
            }

            constraints[index] = new Constraint(comparisonOperator, version);
        }

        range = new PackageVersionRange(constraints);
        return true;
    }

    public bool IsSatisfiedBy(SemanticVersion version)
    {
        if (_constraints is null)
        {
            return false;
        }

        foreach (var constraint in _constraints)
        {
            var comparison = version.CompareTo(constraint.Version);
            if (constraint.Operator switch
                {
                    ComparisonOperator.Exact or ComparisonOperator.Equal => comparison != 0,
                    ComparisonOperator.LessThan => comparison >= 0,
                    ComparisonOperator.LessThanOrEqual => comparison > 0,
                    ComparisonOperator.GreaterThan => comparison <= 0,
                    ComparisonOperator.GreaterThanOrEqual => comparison < 0,
                    _ => true,
                })
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsSatisfiedBy(string version, string range)
        => SemanticVersion.TryParse(version, out var parsedVersion)
           && TryParse(range, out var parsedRange)
           && parsedRange.IsSatisfiedBy(parsedVersion);

    public static bool TryCompare(string left, string right, out int comparison)
    {
        comparison = 0;
        if (!SemanticVersion.TryParse(left, out var leftVersion)
            || !SemanticVersion.TryParse(right, out var rightVersion))
        {
            return false;
        }

        comparison = leftVersion.CompareTo(rightVersion);
        return true;
    }

    public bool Equals(PackageVersionRange other)
        => string.Equals(ToString(), other.ToString(), StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is PackageVersionRange other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToString());

    public override string ToString()
        => _constraints is null
            ? string.Empty
            : string.Join(' ', _constraints.Select(static constraint => constraint.ToString()));

    private static ComparisonOperator ReadOperator(string token, out string version)
    {
        if (token.StartsWith(">=", StringComparison.Ordinal))
        {
            version = token[2..];
            return ComparisonOperator.GreaterThanOrEqual;
        }

        if (token.StartsWith("<=", StringComparison.Ordinal))
        {
            version = token[2..];
            return ComparisonOperator.LessThanOrEqual;
        }

        if (token.StartsWith('>'))
        {
            version = token[1..];
            return ComparisonOperator.GreaterThan;
        }

        if (token.StartsWith('<'))
        {
            version = token[1..];
            return ComparisonOperator.LessThan;
        }

        if (token.StartsWith('='))
        {
            version = token[1..];
            return ComparisonOperator.Equal;
        }

        version = token;
        return ComparisonOperator.Exact;
    }

    private enum ComparisonOperator
    {
        Exact,
        Equal,
        LessThan,
        LessThanOrEqual,
        GreaterThan,
        GreaterThanOrEqual,
    }

    private readonly record struct Constraint(ComparisonOperator Operator, SemanticVersion Version)
    {
        public override string ToString()
            => Operator switch
            {
                ComparisonOperator.Exact => Version.ToString(),
                ComparisonOperator.Equal => $"={Version}",
                ComparisonOperator.LessThan => $"<{Version}",
                ComparisonOperator.LessThanOrEqual => $"<={Version}",
                ComparisonOperator.GreaterThan => $">{Version}",
                ComparisonOperator.GreaterThanOrEqual => $">={Version}",
                _ => throw new InvalidOperationException(),
            };
    }
}
