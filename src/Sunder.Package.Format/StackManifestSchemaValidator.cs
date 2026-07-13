using System.Text.RegularExpressions;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Format;

internal static class StackManifestSchemaValidator
{
    private static readonly Regex StackIdRegex = new("^[a-z0-9]+([.-][a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FragmentIdRegex = new("^[a-z0-9]+([._-][a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TagRegex = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void Validate(
        SunderStackManifest? manifest,
        string stagingPath,
        ICollection<string> errors)
    {
        if (manifest is null)
        {
            errors.Add("Stack manifest is empty or invalid.");
            return;
        }

        if (manifest.SchemaVersion != SunderStackFormat.CurrentSchemaVersion)
        {
            errors.Add($"Stack manifest must declare schemaVersion {SunderStackFormat.CurrentSchemaVersion}.");
        }

        if (manifest.MinReaderVersion is not null && manifest.MinReaderVersion > SunderStackFormat.CurrentReaderVersion)
        {
            errors.Add($"Stack manifest requires reader version {manifest.MinReaderVersion}, but this Sunder reader supports {SunderStackFormat.CurrentReaderVersion}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.StackId) || !StackIdRegex.IsMatch(manifest.StackId))
        {
            errors.Add($"Stack id '{manifest.StackId}' must use lowercase ASCII identifiers separated by dot or dash.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add($"Stack manifest for '{manifest.StackId ?? stagingPath}' is missing name.");
        }

        var packages = manifest.Packages ?? [];
        var fragments = manifest.Fragments ?? [];
        if (packages.Count == 0 && fragments.Count == 0)
        {
            errors.Add("Stack manifest must include at least one package requirement or fragment.");
        }

        ValidatePackages(packages, errors);
        ValidateFragments(fragments, stagingPath, errors);
    }

    private static void ValidatePackages(
        IReadOnlyList<SunderStackPackageRequirement> packages,
        ICollection<string> errors)
    {
        var seenPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            if (!PackageId.TryParse(package.PackageId, out _))
            {
                errors.Add($"Stack package id '{package.PackageId}' must use lowercase dot-separated ASCII identifiers.");
            }
            else if (!seenPackages.Add(package.PackageId!))
            {
                errors.Add($"Stack package id '{package.PackageId}' is declared more than once.");
            }

            if (string.IsNullOrWhiteSpace(package.InstallTag) || !TagRegex.IsMatch(package.InstallTag))
            {
                errors.Add($"Stack package '{package.PackageId ?? "unknown"}' must declare a valid installTag.");
            }

            if (!string.IsNullOrWhiteSpace(package.CreatedWithVersion) && !SemanticVersion.TryParse(package.CreatedWithVersion, out _))
            {
                errors.Add($"Stack package '{package.PackageId ?? "unknown"}' has invalid createdWithVersion '{package.CreatedWithVersion}'.");
            }

            if (!string.IsNullOrWhiteSpace(package.MinimumVersion) && !SemanticVersion.TryParse(package.MinimumVersion, out _))
            {
                errors.Add($"Stack package '{package.PackageId ?? "unknown"}' has invalid minimumVersion '{package.MinimumVersion}'.");
            }

            if (package.Required is null)
            {
                errors.Add($"Stack package '{package.PackageId ?? "unknown"}' must declare required.");
            }
        }
    }

    private static void ValidateFragments(
        IReadOnlyList<SunderStackFragmentManifest> fragments,
        string stagingPath,
        ICollection<string> errors)
    {
        var seenFragments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fragment in fragments)
        {
            var fragmentId = fragment.FragmentId ?? "unknown";
            if (string.IsNullOrWhiteSpace(fragment.FragmentId) || !FragmentIdRegex.IsMatch(fragment.FragmentId))
            {
                errors.Add($"Stack fragment id '{fragment.FragmentId}' must use lowercase ASCII identifiers separated by dot, dash, or underscore.");
            }
            else if (!seenFragments.Add(fragment.FragmentId))
            {
                errors.Add($"Stack fragment id '{fragment.FragmentId}' is declared more than once.");
            }

            if (!PackageId.TryParse(fragment.OwnerPackageId, out _))
            {
                errors.Add($"Stack fragment '{fragmentId}' ownerPackageId '{fragment.OwnerPackageId}' must use lowercase dot-separated ASCII identifiers.");
            }

            if (string.IsNullOrWhiteSpace(fragment.ContributorId))
            {
                errors.Add($"Stack fragment '{fragmentId}' is missing contributorId.");
            }

            if (string.IsNullOrWhiteSpace(fragment.SchemaId))
            {
                errors.Add($"Stack fragment '{fragmentId}' is missing schemaId.");
            }

            if (fragment.SchemaVersion is null or <= 0)
            {
                errors.Add($"Stack fragment '{fragmentId}' must declare a positive schemaVersion.");
            }

            if (string.IsNullOrWhiteSpace(fragment.DisplayName))
            {
                errors.Add($"Stack fragment '{fragmentId}' is missing displayName.");
            }

            ValidatePayload(fragment, fragmentId, stagingPath, errors);
            ValidateRequiredInputs(fragment.RequiredInputs ?? [], $"Stack fragment '{fragmentId}'", errors);
        }
    }

    private static void ValidatePayload(
        SunderStackFragmentManifest fragment,
        string fragmentId,
        string stagingPath,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(fragment.PayloadPath))
        {
            errors.Add($"Stack fragment '{fragmentId}' is missing payloadPath.");
            return;
        }

        if (!StackArchivePathValidator.TryParse(fragment.PayloadPath, "fragment payloadPath", errors, out var path))
        {
            return;
        }

        if (!path.ToString().StartsWith(SunderStackFormat.PayloadRoot, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Stack fragment '{fragmentId}' payloadPath '{fragment.PayloadPath}' must be under payload/.");
        }
        else if (!File.Exists(SunderArchive.ResolveFile(stagingPath, path)))
        {
            errors.Add($"Stack fragment '{fragmentId}' payloadPath '{fragment.PayloadPath}' was not found in the Stack archive.");
        }
    }

    private static void ValidateRequiredInputs(
        IReadOnlyList<SunderStackRequiredInputManifest> inputs,
        string label,
        ICollection<string> errors)
    {
        var seenInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input.InputId) || !FragmentIdRegex.IsMatch(input.InputId))
            {
                errors.Add($"{label} required input id '{input.InputId}' must use lowercase ASCII identifiers separated by dot, dash, or underscore.");
            }
            else if (!seenInputs.Add(input.InputId))
            {
                errors.Add($"{label} required input id '{input.InputId}' is declared more than once.");
            }

            if (string.IsNullOrWhiteSpace(input.Label))
            {
                errors.Add($"{label} required input '{input.InputId ?? "unknown"}' is missing label.");
            }

            if (input.Required is null)
            {
                errors.Add($"{label} required input '{input.InputId ?? "unknown"}' must declare required.");
            }
        }
    }
}
