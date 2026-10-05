using System.Globalization;
using System.Numerics;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Format;

internal static class PackageDependencyValidator
{
    public static void Validate(
        IReadOnlyList<SunderPackageDependencyManifest>? dependencies,
        ICollection<string> errors)
    {
        if (dependencies is { Count: > SunderPackageFormat.MaxDependencies })
        {
            errors.Add($"Package manifest declares {dependencies.Count} dependencies; the limit is {SunderPackageFormat.MaxDependencies}.");
        }

        var seenDependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in (dependencies ?? []).Take(SunderPackageFormat.MaxDependencies))
        {
            if (dependency is null)
            {
                errors.Add("Package dependency entry is null.");
                continue;
            }
            if (!PackageId.TryParse(dependency.PackageId, out _))
            {
                errors.Add($"Dependency package id '{dependency.PackageId}' must use lowercase dot-separated ASCII identifiers.");
            }
            else if (!seenDependencies.Add(dependency.PackageId!))
            {
                errors.Add($"Dependency package id '{dependency.PackageId}' is declared more than once.");
            }

            if (!PackageVersionRange.TryParse(dependency.VersionRange, out var range)
                || !string.Equals(range.ToString(), dependency.VersionRange, StringComparison.Ordinal))
            {
                errors.Add($"Dependency '{dependency.PackageId ?? "unknown"}' has unsupported or noncanonical versionRange '{dependency.VersionRange}'.");
            }
            else if (IsEmpty(range))
            {
                errors.Add($"Dependency '{dependency.PackageId ?? "unknown"}' versionRange '{dependency.VersionRange}' permits no semantic version.");
            }
            if (dependency.Presence is not ("required" or "optional"))
            {
                errors.Add($"Dependency '{dependency.PackageId ?? "unknown"}' must declare presence as 'required' or 'optional'.");
            }
        }
    }

    private static bool IsEmpty(PackageVersionRange range)
    {
        var lower = SemanticVersion.Parse("0.0.0-0");
        var lowerExclusive = false;
        // The range has already passed the shared parser and canonical whitespace check.
        foreach (var token in range.ToString().Split(' '))
        {
            if (token[0] == '<') continue;
            var version = SemanticVersion.Parse(token.TrimStart('>', '='));
            var exclusive = token[0] == '>' && token[1] != '=';
            var comparison = version.ComparePrecedenceTo(lower);
            if (comparison > 0 || comparison == 0 && exclusive)
            {
                lower = version;
                lowerExclusive = exclusive;
            }
        }

        // The least version meeting all lower bounds is the only witness needed for a conjunction.
        return lowerExclusive
            ? !TryGetSuccessor(lower, out var successor) || !range.IsSatisfiedBy(successor)
            : !range.IsSatisfiedBy(lower);
    }

    private static bool TryGetSuccessor(SemanticVersion version, out SemanticVersion successor)
    {
        // Every permitted precedence has a metadata-free representative, freeing the entire length budget.
        var core = $"{version.Major}.{version.Minor}.{version.Patch}";
        if (version.Prerelease is { } prerelease)
        {
            if (SemanticVersion.TryParse($"{core}-{prerelease}.0", out successor)) return true;

            // Preserve the longest possible identifier prefix, increasing one token and dropping its suffix.
            var identifiers = prerelease.Split('.');
            for (var index = identifiers.Length - 1; index >= 0; index--)
            {
                var prefix = core + "-" + string.Join(".", identifiers, 0, index);
                if (index > 0) prefix += ".";
                var next = NextPrereleaseIdentifier(identifiers[index], SemanticVersion.MaximumLength - prefix.Length);
                if (next is null) continue;
                successor = SemanticVersion.Parse(prefix + next);
                return true;
            }

            successor = SemanticVersion.Parse(core);
            return true;
        }

        // Carry patch -> minor -> major only when that incremented core cannot fit, resetting the suffix.
        string[] components = [version.Major, version.Minor, version.Patch];
        for (var index = components.Length - 1; index >= 0; index--)
        {
            components[index] = (BigInteger.Parse(components[index], CultureInfo.InvariantCulture) + BigInteger.One)
                .ToString(CultureInfo.InvariantCulture);
            var nextCore = string.Join('.', components);
            if (SemanticVersion.TryParse(nextCore + "-0", out successor)
                || SemanticVersion.TryParse(nextCore, out successor)) return true;
            components[index] = "0";
        }

        successor = default;
        return false;
    }

    private static string? NextPrereleaseIdentifier(string identifier, int maximumLength)
    {
        if (identifier.All(char.IsAsciiDigit))
        {
            var next = (BigInteger.Parse(identifier, CultureInfo.InvariantCulture) + BigInteger.One)
                .ToString(CultureInfo.InvariantCulture);
            // All numeric identifiers precede every nonnumeric identifier, whose minimum is "-".
            return next.Length <= maximumLength ? next : "-";
        }
        if (identifier.Length < maximumLength) return identifier + "-";

        const string alphabet = "-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        for (var index = identifier.Length - 1; index >= 0; index--)
        {
            for (var next = alphabet.IndexOf(identifier[index]) + 1; next < alphabet.Length; next++)
            {
                var candidate = identifier[..index] + alphabet[next];
                // Falling into the numeric class would lower precedence, even when ordinally greater.
                if (candidate.All(char.IsAsciiDigit))
                {
                    if (candidate.Length == maximumLength) continue;
                    candidate += "-";
                }
                return candidate;
            }
        }
        return null;
    }
}
