using Sunder.Sdk.Packaging;

namespace Sunder.Package.Format;

internal static class PackageTargetValidator
{
    public static void Validate(
        SunderPackageManifest manifest,
        IEnumerable<ArchiveRelativePath> physicalPaths,
        ICollection<string> errors)
    {
        var paths = physicalPaths.ToArray();
        var targets = manifest.Targets;
        if (targets is null)
        {
            errors.Add("Package manifest is missing targets.");
            targets = [];
        }
        if (targets.Count == 0 && !(manifest.ContractBundles?.Any(static bundle => bundle is not null) ?? false))
        {
            errors.Add("Package manifest must declare at least one target or contract bundle.");
        }
        if (targets.Count > SunderPackageFormat.MaxTargets)
        {
            errors.Add($"Package manifest declares {targets.Count} targets; the limit is {SunderPackageFormat.MaxTargets}.");
        }

        var targetKeys = new HashSet<SunderPackageTargetKey>();
        var targetRoles = new HashSet<string>(StringComparer.Ordinal);
        PackageWebViewValidator.ValidateDeclarations(manifest, errors, "Package");
        foreach (var target in targets.Take(SunderPackageFormat.MaxTargets))
        {
            if (target is null)
            {
                errors.Add("Package target entry is null.");
                continue;
            }

            var hasKey = ValidateKey(target, errors, out var key);
            var firstForKey = hasKey && targetKeys.Add(key);
            if (hasKey && !firstForKey)
            {
                errors.Add($"Package manifest declares target '{key}' more than once.");
            }
            if (hasKey) targetRoles.Add(key.Role);

            if (!SunderPackageFormat.IsTargetKind(target.Role, target.Kind))
            {
                errors.Add($"Package target '{DisplayKey(target)}' declares kind '{target.Kind}' that is invalid for role '{target.Role}'.");
            }

            var hasEntryPoint = PackageArchivePathValidator.TryParse(
                target.EntryPoint,
                $"target '{DisplayKey(target)}' entryPoint",
                errors,
                out var entryPoint,
                required: true);
            if (hasEntryPoint)
            {
                ValidateEntryPointExtension(target, errors);
            }
            ValidateOptionalMetadata(target, errors);
            ValidateCapabilities(target, errors);

            if (!hasKey || !firstForKey) continue;
            try
            {
                var plan = SunderPackageTargetResolver.CreateProjectionPlan(key, paths);
                if (hasEntryPoint && !plan.TryResolveLogicalPath(entryPoint, out _))
                {
                    errors.Add($"Package target '{key}' entry point '{target.EntryPoint}' does not resolve in its payload union.");
                }
                PackageWebViewValidator.ValidateResolvedIcons(target, plan, errors, "Package");
            }
            catch (InvalidDataException exception)
            {
                errors.Add($"Package target '{key}' has an invalid payload union: {exception.Message}");
            }
        }

        ValidateClaimedRoleLayers(paths, targetKeys, targetRoles, errors);
    }

    private static bool ValidateKey(
        SunderPackageTargetManifest target,
        ICollection<string> errors,
        out SunderPackageTargetKey key)
    {
        var valid = true;
        if (!SunderPackageFormat.IsHostRole(target.Role))
        {
            errors.Add($"Package target declares unknown role '{target.Role}'.");
            valid = false;
        }
        if (!SunderPackageFormat.IsRuntimeIdentifier(target.Rid))
        {
            errors.Add($"Package target '{target.Role ?? "unknown"}' declares unknown RID '{target.Rid}'.");
            valid = false;
        }

        if (valid)
        {
            key = new SunderPackageTargetKey(target.Role!, target.Rid!);
            return true;
        }
        key = default;
        return false;
    }

    private static void ValidateEntryPointExtension(
        SunderPackageTargetManifest target,
        ICollection<string> errors)
    {
        var expectedExtension = target.Kind switch
        {
            SunderPackageFormat.AvaloniaTargetKind or SunderPackageFormat.DotnetTargetKind => ".dll",
            SunderPackageFormat.WebTargetKind => ".html",
            _ => null,
        };
        if (expectedExtension is not null
            && !target.EntryPoint!.EndsWith(expectedExtension, StringComparison.Ordinal))
        {
            errors.Add($"Package target '{DisplayKey(target)}' kind '{target.Kind}' entryPoint must end with '{expectedExtension}'.");
        }
    }

    private static void ValidateOptionalMetadata(
        SunderPackageTargetManifest target,
        ICollection<string> errors)
    {
        if (target.TargetFramework is not null && !IsBoundedAsciiValue(target.TargetFramework, 128))
        {
            errors.Add($"Package target '{DisplayKey(target)}' targetFramework must be non-empty portable ASCII of at most 128 characters.");
        }
        if (target.SdkVersion is not null && !SemanticVersion.TryParse(target.SdkVersion, out _))
        {
            errors.Add($"Package target '{DisplayKey(target)}' sdkVersion must be strict SemVer 2.0 when declared.");
        }
    }

    private static void ValidateCapabilities(
        SunderPackageTargetManifest target,
        ICollection<string> errors)
    {
        if (target.RequiredHostCapabilities is null)
        {
            errors.Add($"Package target '{DisplayKey(target)}' is missing requiredHostCapabilities.");
            return;
        }
        if (target.RequiredHostCapabilities.Count > SunderPackageFormat.MaxHostCapabilities)
        {
            errors.Add($"Package target '{DisplayKey(target)}' declares too many requiredHostCapabilities.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in target.RequiredHostCapabilities.Take(SunderPackageFormat.MaxHostCapabilities))
        {
            if (!SunderPackageFormat.IsHostCapabilityId(capability))
            {
                errors.Add($"Package target '{DisplayKey(target)}' declares invalid host capability '{capability}'.");
            }
            else if (!seen.Add(capability!))
            {
                errors.Add($"Package target '{DisplayKey(target)}' declares host capability '{capability}' more than once.");
            }
        }
    }

    private static void ValidateClaimedRoleLayers(
        IEnumerable<ArchiveRelativePath> paths,
        ISet<SunderPackageTargetKey> targetKeys,
        ISet<string> targetRoles,
        ICollection<string> errors)
    {
        foreach (var path in paths)
        {
            if (!SunderPackageFormat.TryClassifyPayloadPath(
                    path.ToString(), out var layer, out var role, out var rid, out _)
                || layer == SunderPackagePayloadLayer.Shared)
            {
                continue;
            }
            if (layer == SunderPackagePayloadLayer.RoleShared && !targetRoles.Contains(role!))
            {
                errors.Add($"Package payload file '{path}' has no declared target for role '{role}'.");
            }
            else if (layer == SunderPackagePayloadLayer.Target
                     && !targetKeys.Contains(new SunderPackageTargetKey(role!, rid!)))
            {
                errors.Add($"Package payload file '{path}' has no matching exact target '{role}/{rid}'.");
            }
        }
    }

    private static bool IsBoundedAsciiValue(string value, int maximumLength)
        => value.Length > 0 && value.Length <= maximumLength
           && value == value.Trim()
           && value.All(static character => character is >= '!' and <= '~');

    private static string DisplayKey(SunderPackageTargetManifest target)
        => $"{target.Role ?? "unknown"}/{target.Rid ?? "unknown"}";
}
