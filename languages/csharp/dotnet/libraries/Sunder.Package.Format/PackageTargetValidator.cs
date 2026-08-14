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

            ValidateKind(target, errors, "Package");

            var hasEntryPoint = PackageArchivePathValidator.TryParse(
                target.EntryPoint,
                $"target '{DisplayKey(target)}' entryPoint",
                errors,
                out var entryPoint,
                required: true);
            if (hasEntryPoint)
            {
                ValidateEntryPointExtension(target, errors, "Package");
            }
            ValidateOptionalMetadata(target, errors, "Package");
            ValidateCapabilities(target, errors, "Package");

            if (!hasKey || !firstForKey) continue;
            try
            {
                var plan = SunderPackageTargetResolver.CreateProjectionPlan(key, paths);
                if (hasEntryPoint)
                {
                    if (!plan.TryResolveLogicalPath(entryPoint, out var physicalEntryPoint))
                    {
                        errors.Add($"Package target '{key}' entry point '{target.EntryPoint}' does not resolve in its payload union.");
                    }
                    else if (IsDotNetWorker(target)
                             && (!SunderPackageFormat.TryClassifyPayloadPath(
                                     physicalEntryPoint.ToString(),
                                     out var layer,
                                     out var role,
                                     out var rid,
                                     out _)
                                 || layer != SunderPackagePayloadLayer.Target
                                 || !string.Equals(role, key.Role, StringComparison.Ordinal)
                                 || !string.Equals(rid, key.Rid, StringComparison.Ordinal)))
                    {
                        errors.Add(
                            $"Package .NET worker target '{key}' entry point '{target.EntryPoint}' must be a file in its exact Runtime/RID payload layer.");
                    }
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

    internal static void ValidateKind(
        SunderPackageTargetManifest target,
        ICollection<string> errors,
        string prefix)
    {
        if (!SunderPackageFormat.IsTargetKind(target.Role, target.Kind))
        {
            errors.Add($"{prefix} target '{DisplayKey(target)}' declares kind '{target.Kind}' that is invalid for role '{target.Role}'.");
        }
    }

    internal static void ValidateEntryPointExtension(
        SunderPackageTargetManifest target,
        ICollection<string> errors,
        string prefix)
    {
        var expectedExtension = target.Kind switch
        {
            SunderPackageFormat.AvaloniaTargetKind => ".dll",
            SunderPackageFormat.WebTargetKind => ".html",
            _ => null,
        };
        if (expectedExtension is not null
            && !target.EntryPoint!.EndsWith(expectedExtension, StringComparison.Ordinal))
        {
            errors.Add($"{prefix} target '{DisplayKey(target)}' kind '{target.Kind}' entryPoint must end with '{expectedExtension}'.");
        }
        if (IsDotNetWorker(target))
        {
            var entryPoint = target.EntryPoint!;
            var fileName = entryPoint.StartsWith("lib/", StringComparison.Ordinal)
                ? entryPoint["lib/".Length..]
                : string.Empty;
            var expectedWindowsExecutable = target.Rid?.StartsWith("win-", StringComparison.Ordinal) == true;
            if (fileName.Length == 0 || fileName.Contains('/'))
            {
                errors.Add(
                    $"{prefix} .NET worker target '{DisplayKey(target)}' entryPoint must be one apphost file directly under 'lib/'.");
            }
            else if (expectedWindowsExecutable
                     && !fileName.EndsWith(".exe", StringComparison.Ordinal))
            {
                errors.Add(
                    $"{prefix} .NET worker target '{DisplayKey(target)}' entryPoint must use the '.exe' apphost extension for its Windows RID.");
            }
            else if (!expectedWindowsExecutable
                     && (fileName.EndsWith(".exe", StringComparison.Ordinal)
                         || fileName.EndsWith(".dll", StringComparison.Ordinal)))
            {
                errors.Add(
                    $"{prefix} .NET worker target '{DisplayKey(target)}' entryPoint must be the extensionless apphost for its non-Windows RID.");
            }
        }
    }

    internal static void ValidateOptionalMetadata(
        SunderPackageTargetManifest target,
        ICollection<string> errors,
        string prefix)
    {
        if (target.TargetFramework is not null && !IsBoundedAsciiValue(target.TargetFramework, 128))
        {
            errors.Add($"{prefix} target '{DisplayKey(target)}' targetFramework must be non-empty portable ASCII of at most 128 characters.");
        }
        if (target.SdkVersion is not null && !SemanticVersion.TryParse(target.SdkVersion, out _))
        {
            errors.Add($"{prefix} target '{DisplayKey(target)}' sdkVersion must be strict SemVer 2.0 when declared.");
        }
        else if (string.Equals(target.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal)
                 && target.SdkVersion is null)
        {
            errors.Add($"{prefix} Runtime target '{DisplayKey(target)}' must declare sdkVersion.");
        }
    }

    internal static void ValidateCapabilities(
        SunderPackageTargetManifest target,
        ICollection<string> errors,
        string prefix)
    {
        if (target.RequiredHostCapabilities is null)
        {
            errors.Add($"{prefix} target '{DisplayKey(target)}' is missing requiredHostCapabilities.");
            return;
        }
        if (target.RequiredHostCapabilities.Count > SunderPackageFormat.MaxHostCapabilities)
        {
            errors.Add($"{prefix} target '{DisplayKey(target)}' declares too many requiredHostCapabilities.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in target.RequiredHostCapabilities.Take(SunderPackageFormat.MaxHostCapabilities))
        {
            if (!SunderPackageFormat.IsHostCapabilityId(capability))
            {
                errors.Add($"{prefix} target '{DisplayKey(target)}' declares invalid host capability '{capability}'.");
            }
            else if (!seen.Add(capability!))
            {
                errors.Add($"{prefix} target '{DisplayKey(target)}' declares host capability '{capability}' more than once.");
            }
        }

        if (!string.Equals(target.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal))
        {
            return;
        }

        RequireRuntimeCapability(target, seen, SunderPackageFormat.RpcV2Capability, errors, prefix);
        RequireRuntimeCapability(target, seen, SunderPackageFormat.WorkerProtocolV3Capability, errors, prefix);
        foreach (var capability in seen.Where(static capability =>
                     capability.StartsWith("rpc.", StringComparison.Ordinal)
                     && capability != SunderPackageFormat.RpcV2Capability))
        {
            errors.Add(
                $"{prefix} Runtime target '{DisplayKey(target)}' cannot require unsupported RPC capability '{capability}'.");
        }
        foreach (var capability in seen.Where(static capability =>
                     capability.StartsWith("worker-protocol.", StringComparison.Ordinal)
                     && capability != SunderPackageFormat.WorkerProtocolV3Capability))
        {
            errors.Add(
                $"{prefix} Runtime target '{DisplayKey(target)}' cannot require unsupported worker protocol capability '{capability}'.");
        }

        if (!SemanticVersion.TryParse(target.SdkVersion, out var sdkVersion))
        {
            return;
        }

        var expectedBaseline = $"sdk-baseline-{sdkVersion.Major}-{sdkVersion.Minor}.v1";
        RequireRuntimeCapability(target, seen, expectedBaseline, errors, prefix);
        foreach (var capability in seen.Where(static value => value.StartsWith("sdk-baseline-", StringComparison.Ordinal)))
        {
            if (!string.Equals(capability, expectedBaseline, StringComparison.Ordinal))
            {
                errors.Add(
                    $"{prefix} Runtime target '{DisplayKey(target)}' SDK baseline capability '{capability}' does not match sdkVersion '{target.SdkVersion}'.");
            }
        }
    }

    private static void RequireRuntimeCapability(
        SunderPackageTargetManifest target,
        IReadOnlySet<string> capabilities,
        string capability,
        ICollection<string> errors,
        string prefix)
    {
        if (!capabilities.Contains(capability))
        {
            errors.Add($"{prefix} Runtime target '{DisplayKey(target)}' must require Host capability '{capability}'.");
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

    private static bool IsDotNetWorker(SunderPackageTargetManifest target)
        => string.Equals(target.Kind, SunderPackageFormat.WorkerTargetKind, StringComparison.Ordinal)
           && target.TargetFramework is { Length: > 3 } targetFramework
           && targetFramework.StartsWith("net", StringComparison.Ordinal)
           && char.IsAsciiDigit(targetFramework[3]);

    private static string DisplayKey(SunderPackageTargetManifest target)
        => $"{target.Role ?? "unknown"}/{target.Rid ?? "unknown"}";
}
