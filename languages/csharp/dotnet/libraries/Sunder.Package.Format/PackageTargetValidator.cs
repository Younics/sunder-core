using System.Numerics;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Format;

internal static class PackageTargetValidator
{
    public static void Validate(
        SunderPackageManifest manifest,
        IEnumerable<ArchiveRelativePath> physicalPaths,
        ICollection<string> errors,
        IReadOnlySet<SunderPackageTargetKey>? acquiredTargets = null)
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
        var workerTargets = new List<SunderPackageTargetManifest>();
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
            ValidateExecutionRuntime(target, errors, "Package");
            if (target.Role == SunderPackageFormat.RuntimeHostRole
                && target.Kind == SunderPackageFormat.WorkerTargetKind)
            {
                workerTargets.Add(target);
            }

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

            if (!hasKey || !firstForKey || acquiredTargets is not null && !acquiredTargets.Contains(key)) continue;
            try
            {
                var plan = SunderPackageTargetResolver.CreateProjectionPlan(key, paths);
                if (hasEntryPoint)
                {
                    if (!plan.TryResolveLogicalPath(entryPoint, out var physicalEntryPoint))
                    {
                        errors.Add($"Package target '{key}' entry point '{target.EntryPoint}' does not resolve in its payload union.");
                    }
                }
                PackageWebViewValidator.ValidateResolvedIcons(target, plan, errors, "Package");
            }
            catch (InvalidDataException exception)
            {
                errors.Add($"Package target '{key}' has an invalid payload union: {exception.Message}");
            }
        }

        ValidateWorkerAgreement(workerTargets, errors);
        if (acquiredTargets is not null)
        {
            if (acquiredTargets.Any(key => !targetKeys.Contains(key)))
                errors.Add("Installed projection acquisition target is not declared in its complete source manifest.");
            targetKeys.IntersectWith(acquiredTargets);
            targetRoles.IntersectWith(targetKeys.Select(key => key.Role));
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
        var moduleAbi = target.ExecutionRuntime?.ModuleAbi;
        if (string.Equals(moduleAbi, SunderPackageFormat.DotNetWorkerModuleAbi, StringComparison.Ordinal)
            && !target.EntryPoint!.EndsWith(".dll", StringComparison.Ordinal))
        {
            errors.Add($"{prefix} .NET Worker target '{DisplayKey(target)}' entryPoint must end with '.dll'.");
        }
        else if (string.Equals(moduleAbi, SunderPackageFormat.NodeWorkerModuleAbi, StringComparison.Ordinal)
                 && !target.EntryPoint!.EndsWith(".cjs", StringComparison.Ordinal))
        {
            errors.Add($"{prefix} Node Worker target '{DisplayKey(target)}' entryPoint must end with '.cjs'.");
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

        if (string.Equals(target.ExecutionRuntime?.ModuleAbi, SunderPackageFormat.DotNetWorkerModuleAbi, StringComparison.Ordinal)
            && !IsDotNetTargetFramework(target.TargetFramework))
        {
            errors.Add($"{prefix} .NET Worker target '{DisplayKey(target)}' must declare a net<major>.<minor> targetFramework.");
        }
        else if (string.Equals(target.ExecutionRuntime?.ModuleAbi, SunderPackageFormat.NodeWorkerModuleAbi, StringComparison.Ordinal)
                 && !IsNodeTargetFramework(target.TargetFramework))
        {
            errors.Add($"{prefix} Node Worker target '{DisplayKey(target)}' must declare a node<major> targetFramework.");
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

        RequireRuntimeCapability(target, seen, SunderPackageFormat.ExecutionRuntimeV1Capability, errors, prefix);
        RequireRuntimeCapability(target, seen, SunderPackageFormat.RpcV1Capability, errors, prefix);
        RequireRuntimeCapability(target, seen, SunderPackageFormat.WorkerProtocolV1Capability, errors, prefix);
        foreach (var capability in seen.Where(static capability =>
                     capability.StartsWith("rpc.", StringComparison.Ordinal)
                     && capability != SunderPackageFormat.RpcV1Capability
                      && capability != SunderPackageFormat.RpcExtendedDeadlineV1Capability
                      && capability != SunderPackageFormat.RpcCallOwnershipV1Capability))
        {
            errors.Add(
                $"{prefix} Runtime target '{DisplayKey(target)}' cannot require unsupported RPC capability '{capability}'.");
        }
        foreach (var capability in seen.Where(static capability =>
                     capability.StartsWith("worker-protocol.", StringComparison.Ordinal)
                     && capability != SunderPackageFormat.WorkerProtocolV1Capability))
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

    internal static void ValidateExecutionRuntime(
        SunderPackageTargetManifest target,
        ICollection<string> errors,
        string prefix)
    {
        var isWorker = string.Equals(target.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal)
                       && string.Equals(target.Kind, SunderPackageFormat.WorkerTargetKind, StringComparison.Ordinal);
        if (!isWorker)
        {
            if (target.ExecutionRuntime is not null)
            {
                errors.Add($"{prefix} target '{DisplayKey(target)}' cannot declare executionRuntime.");
            }
            return;
        }

        var requirement = target.ExecutionRuntime;
        if (requirement is null)
        {
            errors.Add($"{prefix} Worker target '{DisplayKey(target)}' is missing executionRuntime.");
            return;
        }
        if (!SunderPackageFormat.IsExecutionRuntimeIdentifier(requirement.Id))
        {
            errors.Add($"{prefix} Worker target '{DisplayKey(target)}' executionRuntime.id is invalid.");
        }
        if (!SunderPackageFormat.IsExecutionRuntimeIdentifier(requirement.ModuleAbi))
        {
            errors.Add($"{prefix} Worker target '{DisplayKey(target)}' executionRuntime.moduleAbi is invalid.");
        }
        if (!IsBoundedStableMajorRange(requirement.VersionRange))
        {
            errors.Add(
                $"{prefix} Worker target '{DisplayKey(target)}' executionRuntime.versionRange must be a nonempty stable SemVer range bounded to one major line.");
        }

        var isDotNetProfile = string.Equals(
                                  target.TargetFramework,
                                  SunderPackageFormat.DotNetWorkerTargetFramework,
                                  StringComparison.Ordinal)
                              || string.Equals(requirement.Id, SunderPackageFormat.DotNetExecutionRuntimeId, StringComparison.Ordinal)
                              || string.Equals(requirement.ModuleAbi, SunderPackageFormat.DotNetWorkerModuleAbi, StringComparison.Ordinal);
        if (!isDotNetProfile) return;
        if (!string.Equals(target.TargetFramework, SunderPackageFormat.DotNetWorkerTargetFramework, StringComparison.Ordinal))
        {
            errors.Add(
                $"{prefix} .NET Worker target '{DisplayKey(target)}' targetFramework must be '{SunderPackageFormat.DotNetWorkerTargetFramework}'.");
        }
        if (!string.Equals(requirement.Id, SunderPackageFormat.DotNetExecutionRuntimeId, StringComparison.Ordinal)
            || !string.Equals(requirement.VersionRange, SunderPackageFormat.DotNetExecutionRuntimeVersionRange, StringComparison.Ordinal)
            || !string.Equals(requirement.ModuleAbi, SunderPackageFormat.DotNetWorkerModuleAbi, StringComparison.Ordinal))
        {
            errors.Add(
                $"{prefix} .NET Worker target '{DisplayKey(target)}' must require "
                + $"'{SunderPackageFormat.DotNetExecutionRuntimeId} {SunderPackageFormat.DotNetExecutionRuntimeVersionRange} {SunderPackageFormat.DotNetWorkerModuleAbi}'.");
        }
    }

    internal static void ValidateWorkerAgreement(
        IReadOnlyList<SunderPackageTargetManifest> workerTargets,
        ICollection<string> errors)
    {
        if (workerTargets.Count < 2) return;

        var first = workerTargets[0];
        foreach (var target in workerTargets.Skip(1))
        {
            if (!string.Equals(target.ExecutionRuntime?.Id, first.ExecutionRuntime?.Id, StringComparison.Ordinal)
                || !string.Equals(target.ExecutionRuntime?.VersionRange, first.ExecutionRuntime?.VersionRange, StringComparison.Ordinal)
                || !string.Equals(target.ExecutionRuntime?.ModuleAbi, first.ExecutionRuntime?.ModuleAbi, StringComparison.Ordinal)
                || !string.Equals(target.TargetFramework, first.TargetFramework, StringComparison.Ordinal)
                || !string.Equals(target.SdkVersion, first.SdkVersion, StringComparison.Ordinal)
                || !string.Equals(target.EntryPoint, first.EntryPoint, StringComparison.Ordinal))
            {
                errors.Add("Package Worker targets must agree on executionRuntime, targetFramework, sdkVersion, and entryPoint.");
                return;
            }
        }
    }

    internal static bool IsBoundedStableMajorRange(string? value)
    {
        if (!PackageVersionRange.TryParse(value, out _)) return false;

        var constraints = value!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(static token => RangeConstraint.TryParse(token, out var constraint)
                ? (RangeConstraint?)constraint
                : null)
            .ToArray();
        if (constraints.Any(static constraint => constraint is null)) return false;

        var parsed = constraints.Select(static constraint => constraint!.Value).ToArray();
        if (parsed.Any(static constraint => constraint.Version.Prerelease is not null
                                            || constraint.Version.BuildMetadata is not null))
        {
            return false;
        }

        var equality = parsed.Where(static constraint => constraint.Operator is RangeOperator.Exact or RangeOperator.Equal).ToArray();
        if (equality.Length > 0)
        {
            return equality.Length == 1 && parsed.Length == 1;
        }

        RangeConstraint? lower = null;
        RangeConstraint? upper = null;
        foreach (var constraint in parsed)
        {
            if (constraint.Operator is RangeOperator.GreaterThan or RangeOperator.GreaterThanOrEqual)
            {
                if (lower is null || constraint.Version > lower.Value.Version
                    || constraint.Version == lower.Value.Version && constraint.Operator == RangeOperator.GreaterThan)
                {
                    lower = constraint;
                }
            }
            else if (constraint.Operator is RangeOperator.LessThan or RangeOperator.LessThanOrEqual)
            {
                if (upper is null || constraint.Version < upper.Value.Version
                    || constraint.Version == upper.Value.Version && constraint.Operator == RangeOperator.LessThan)
                {
                    upper = constraint;
                }
            }
        }

        if (lower is null || upper is null) return false;

        var major = BigInteger.Parse(lower.Value.Version.Major, System.Globalization.CultureInfo.InvariantCulture);
        var upperMajor = BigInteger.Parse(upper.Value.Version.Major, System.Globalization.CultureInfo.InvariantCulture);
        if (upperMajor < major || upperMajor > major + BigInteger.One) return false;
        if (upperMajor == major + BigInteger.One
            && (upper.Value.Operator != RangeOperator.LessThan
                || upper.Value.Version.Minor != "0"
                || upper.Value.Version.Patch != "0"))
        {
            return false;
        }

        var candidate = lower.Value.Operator == RangeOperator.GreaterThan
            ? SemanticVersion.Parse(
                $"{lower.Value.Version.Major}.{lower.Value.Version.Minor}.{BigInteger.Parse(lower.Value.Version.Patch, System.Globalization.CultureInfo.InvariantCulture) + BigInteger.One}")
            : lower.Value.Version;
        return parsed.All(constraint => constraint.IsSatisfiedBy(candidate));
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

    private static bool IsDotNetTargetFramework(string? value)
    {
        if (value is not { Length: > 4 } || !value.StartsWith("net", StringComparison.Ordinal)) return false;
        var parts = value[3..].Split('.');
        return parts.Length == 2 && parts.All(static part => part.Length > 0 && part.All(char.IsAsciiDigit));
    }

    private static bool IsNodeTargetFramework(string? value)
        => value is { Length: > 4 }
           && value.StartsWith("node", StringComparison.Ordinal)
           && value[4..].All(char.IsAsciiDigit);

    private enum RangeOperator
    {
        Exact,
        Equal,
        LessThan,
        LessThanOrEqual,
        GreaterThan,
        GreaterThanOrEqual,
    }

    private readonly record struct RangeConstraint(RangeOperator Operator, SemanticVersion Version)
    {
        internal static bool TryParse(string token, out RangeConstraint constraint)
        {
            var (operation, versionText) = token switch
            {
                _ when token.StartsWith(">=", StringComparison.Ordinal) => (RangeOperator.GreaterThanOrEqual, token[2..]),
                _ when token.StartsWith("<=", StringComparison.Ordinal) => (RangeOperator.LessThanOrEqual, token[2..]),
                _ when token.StartsWith('>') => (RangeOperator.GreaterThan, token[1..]),
                _ when token.StartsWith('<') => (RangeOperator.LessThan, token[1..]),
                _ when token.StartsWith('=') => (RangeOperator.Equal, token[1..]),
                _ => (RangeOperator.Exact, token),
            };
            if (!SemanticVersion.TryParse(versionText, out var version))
            {
                constraint = default;
                return false;
            }
            constraint = new RangeConstraint(operation, version);
            return true;
        }

        internal bool IsSatisfiedBy(SemanticVersion candidate)
        {
            var comparison = candidate.ComparePrecedenceTo(Version);
            return Operator switch
            {
                RangeOperator.Exact or RangeOperator.Equal => comparison == 0,
                RangeOperator.LessThan => comparison < 0,
                RangeOperator.LessThanOrEqual => comparison <= 0,
                RangeOperator.GreaterThan => comparison > 0,
                RangeOperator.GreaterThanOrEqual => comparison >= 0,
                _ => false,
            };
        }
    }

    private static string DisplayKey(SunderPackageTargetManifest target)
        => $"{target.Role ?? "unknown"}/{target.Rid ?? "unknown"}";
}
