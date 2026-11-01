using Sunder.Sdk.Packaging;

namespace Sunder.Package.Format;

public static class SunderStackPackageRequirements
{
    /// <summary>Merges declarations without weakening a minimum or choosing between conflicting install tags.</summary>
    public static IReadOnlyList<SunderStackPackageRequirement> Merge(IEnumerable<SunderStackPackageRequirement> requirements)
        => requirements.GroupBy(requirement => requirement.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                if (!PackageId.TryParse(first.PackageId, out _))
                    throw new InvalidDataException("Stack package requirement has an invalid package id.");
                var tags = group.Select(value => string.IsNullOrWhiteSpace(value.InstallTag) ? "latest" : value.InstallTag.Trim())
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (tags.Length != 1)
                    throw new InvalidDataException($"Stack package '{first.PackageId}' declares conflicting install tags: {string.Join(", ", tags)}.");
                SemanticVersion? minimum = null;
                foreach (var value in group.Select(value => value.MinimumVersion).Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    if (!SemanticVersion.TryParse(value, out var parsed))
                        throw new InvalidDataException($"Stack package '{first.PackageId}' declares invalid minimum version '{value}'.");
                    if (minimum is null || parsed.ComparePrecedenceTo(minimum.Value) > 0) minimum = parsed;
                }
                return new SunderStackPackageRequirement
                {
                    PackageId = first.PackageId,
                    InstallTag = tags[0],
                    CreatedWithVersion = group.Select(value => value.CreatedWithVersion).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                    MinimumVersion = minimum?.ToString(),
                    Required = group.Any(value => value.Required != false),
                };
            }).OrderBy(value => value.PackageId, StringComparer.Ordinal).ToArray();
}
