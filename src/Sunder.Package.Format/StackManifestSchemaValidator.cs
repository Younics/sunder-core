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
        ICollection<string> warnings,
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

        ValidateFeatures(manifest.Features, manifest.RequiredFeatures, warnings, errors);

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

    private static void ValidateFeatures(
        IReadOnlyList<string>? features,
        IReadOnlyList<string>? requiredFeatures,
        ICollection<string> warnings,
        ICollection<string> errors)
    {
        ValidateFeatureList(features, "feature", errors);
        ValidateFeatureList(requiredFeatures, "required feature", errors);
        foreach (var feature in features ?? [])
        {
            if (feature is null)
            {
                continue;
            }
            if (!SunderStackFormat.SupportedFeatures.Contains(feature))
            {
                warnings.Add($"Stack declares unknown optional feature '{feature}', which this reader will ignore.");
            }
        }
        foreach (var feature in requiredFeatures ?? [])
        {
            if (feature is null)
            {
                continue;
            }
            if (!SunderStackFormat.SupportedFeatures.Contains(feature))
            {
                errors.Add($"Stack requires unsupported feature '{feature}'.");
            }
        }
    }

    private static void ValidateFeatureList(
        IReadOnlyList<string>? features,
        string label,
        ICollection<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in features ?? [])
        {
            if (feature is null)
            {
                errors.Add($"Stack {label} entry is null.");
                continue;
            }
            if (!SunderPackageFormat.IsSdkCapabilityId(feature))
            {
                errors.Add($"Stack {label} '{feature}' must be a lowercase versioned feature id such as 'media.v1'.");
            }
            else if (!seen.Add(feature))
            {
                errors.Add($"Stack {label} '{feature}' is declared more than once.");
            }
        }
    }

    private static void ValidatePackages(
        IReadOnlyList<SunderStackPackageRequirement> packages,
        ICollection<string> errors)
    {
        var seenPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            if (package is null)
            {
                errors.Add("Stack package requirement is null.");
                continue;
            }
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
            if (fragment is null)
            {
                errors.Add("Stack fragment entry is null.");
                continue;
            }
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
            ValidatePreview(fragment.Preview, $"Stack fragment '{fragmentId}'", errors);
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

        if (!path.ToString().StartsWith(SunderStackFormat.FragmentPayloadRoot, StringComparison.Ordinal))
        {
            errors.Add($"Stack fragment '{fragmentId}' payloadPath '{fragment.PayloadPath}' must be under {SunderStackFormat.FragmentPayloadRoot}.");
        }
        else if (!File.Exists(SunderArchive.ResolveFile(stagingPath, path)))
        {
            errors.Add($"Stack fragment '{fragmentId}' payloadPath '{fragment.PayloadPath}' was not found in the Stack archive.");
        }
        else
        {
            ValidateFragmentJson(fragmentId, SunderArchive.ResolveFile(stagingPath, path), errors);
        }
    }

    private static void ValidateFragmentJson(string fragmentId, string payloadPath, ICollection<string> errors)
    {
        const long maxFragmentJsonBytes = 4L * 1024L * 1024L;
        if (new FileInfo(payloadPath).Length > maxFragmentJsonBytes)
        {
            errors.Add($"Stack fragment '{fragmentId}' JSON payload must be 4 MiB or smaller.");
            return;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(payloadPath));
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                errors.Add($"Stack fragment '{fragmentId}' payload must contain one JSON object.");
            }
        }
        catch (System.Text.Json.JsonException exception)
        {
            errors.Add($"Stack fragment '{fragmentId}' payload is not strict JSON: {exception.Message}");
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
            if (input is null)
            {
                errors.Add($"{label} required input entry is null.");
                continue;
            }
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

            if (input.Sensitivity is not ("Public" or "Secret"))
            {
                errors.Add($"{label} required input '{input.InputId ?? "unknown"}' must declare sensitivity as Public or Secret.");
            }
            else if (input.Sensitivity == "Secret" && input.DefaultValue is not null)
            {
                errors.Add($"{label} secret required input '{input.InputId ?? "unknown"}' must not declare a portable default value.");
            }

            if (input.Required is null)
            {
                errors.Add($"{label} required input '{input.InputId ?? "unknown"}' must declare required.");
            }
        }
    }

    private static void ValidatePreview(
        SunderStackFragmentPreview? preview,
        string label,
        ICollection<string> errors)
    {
        foreach (var detail in preview?.DisplayDetails ?? [])
        {
            if (detail is null)
            {
                errors.Add($"{label} preview display detail is null.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(detail.Label) || string.IsNullOrWhiteSpace(detail.Value))
            {
                errors.Add($"{label} preview display detail must declare label and value.");
            }
        }
    }
}
