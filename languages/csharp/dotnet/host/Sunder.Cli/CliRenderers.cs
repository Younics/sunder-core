using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Cli;

internal static class CliRenderers
{
    public static void PackageSummaries(CliOutput output, IReadOnlyList<RegistryPackageSummary> packages)
    {
        var idWidth = Math.Max("Package".Length, packages.Max(package => package.PackageId.Length));
        var versionWidth = Math.Max("Latest".Length, packages.Max(package => package.LatestVersion?.Length ?? 1));
        output.Line($"{"Package".PadRight(idWidth)}  {"Latest".PadRight(versionWidth)}  Summary");
        foreach (var package in packages)
            output.Line($"{package.PackageId.PadRight(idWidth)}  {(package.LatestVersion ?? "-").PadRight(versionWidth)}  {package.Summary ?? string.Empty}");
    }

    public static void InstalledPackages(CliOutput output, IReadOnlyList<InstalledPackageDescriptor> packages)
    {
        var idWidth = Math.Max("Package".Length, packages.Max(package => package.PackageId.Length));
        var versionWidth = Math.Max("Version".Length, packages.Max(package => package.Version.Length));
        output.Line($"{"Package".PadRight(idWidth)}  {"Version".PadRight(versionWidth)}  Enabled  Summary");
        foreach (var package in packages)
            output.Line($"{package.PackageId.PadRight(idWidth)}  {package.Version.PadRight(versionWidth)}  {(package.IsEnabled ? "yes" : "no ")}      {package.Summary ?? string.Empty}");
    }

    public static void PackageDetails(CliOutput output, RegistryPackageDetails package)
    {
        output.Line($"Package: {package.PackageId}");
        output.Line($"Name: {package.Name}");
        output.Line($"Latest: {package.LatestVersion ?? "-"}");
        if (!string.IsNullOrWhiteSpace(package.Summary)) output.Line($"Summary: {package.Summary}");
        output.Line("Versions:");
        foreach (var version in package.Versions
                     .OrderByDescending(version => SemanticVersion.TryParse(version.Version, out var parsed) ? parsed : (SemanticVersion?)null)
                     .ThenByDescending(version => version.Version, StringComparer.Ordinal))
        {
            var suffix = version.IsYanked ? " yanked" : string.Empty;
            if (!string.IsNullOrWhiteSpace(version.DeprecatedMessage)) suffix += $" deprecated: {version.DeprecatedMessage}";
            output.Line($"  {version.Version}{suffix}");
        }
    }

    public static void PackageVersion(CliOutput output, RegistryPackageVersionDetails version)
    {
        output.Line($"Package: {version.PackageId}");
        output.Line($"Name: {version.Name}");
        output.Line($"Version: {version.Version}");
        if (!string.IsNullOrWhiteSpace(version.Summary)) output.Line($"Summary: {version.Summary}");
        output.Line($"Manifest Format Version: {version.ManifestFormatVersion}");
        output.Line($"Archive Format Version: {version.ArchiveFormatVersion}");
        if (!string.IsNullOrWhiteSpace(version.DeprecatedMessage)) output.Line($"Deprecated: {version.DeprecatedMessage}");
        output.Line("Targets:");
        foreach (var target in version.Targets
                     .OrderBy(target => target.Role == "runtime" ? 0 : 1)
                     .ThenBy(target => target.Rid, StringComparer.Ordinal))
        {
            output.Line($"  {target.Role}/{target.Rid}  {target.Kind}  {target.EntryPoint}  {target.TargetFramework ?? "-"}");
        }
        if (version.Targets.Count == 0) output.Line("  none (shared content and contract bundles only)");
        output.Line("Dependencies:");
        foreach (var dependency in version.DependsOn.OrderBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase))
            output.Line($"  {dependency.PackageId} {dependency.VersionRange}");
        if (version.DependsOn.Count == 0) output.Line("  none");
        output.Line("Contracts:");
        foreach (var contract in version.ContractBundles.OrderBy(item => item.ContractId, StringComparer.Ordinal))
            output.Line($"  bundle {contract.ContractId} {contract.Version} ({contract.Exposure})");
        foreach (var contract in version.UsesContracts
                     .OrderBy(item => item.Role, StringComparer.Ordinal)
                     .ThenBy(item => item.ContractId, StringComparer.Ordinal))
            output.Line($"  uses {contract.Role} {contract.ContractId} {contract.ContractVersion} ({(contract.Required ? "required" : "optional")}; {string.Join(',', contract.Actions)})");
        foreach (var provider in version.Providers
                     .OrderBy(item => item.Role, StringComparer.Ordinal)
                     .ThenBy(item => item.ProviderId, StringComparer.Ordinal))
            output.Line($"  provides {provider.Role} {provider.ProviderId} -> {provider.ContractId} {provider.ContractVersion} ({provider.Audience})");
        if (version.ContractBundles.Count == 0 && version.UsesContracts.Count == 0 && version.Providers.Count == 0) output.Line("  none");
        output.Line($"Canonical SHA-256: {version.CanonicalArtifact.Sha256}");
        output.Line($"Canonical Size: {version.CanonicalArtifact.Size} bytes");
        output.Line($"Projection Artifacts: {version.Projections.Count}");
    }

    public static void StackSummaries(CliOutput output, IReadOnlyList<RegistryStackSummary> stacks)
    {
        var idWidth = Math.Max("Stack".Length, stacks.Max(stack => stack.StackId.Length));
        var packageWidth = Math.Max("Packages".Length, stacks.Max(stack => stack.PackageCount.ToString(System.Globalization.CultureInfo.InvariantCulture).Length));
        output.Line($"{"Stack".PadRight(idWidth)}  {"Packages".PadRight(packageWidth)}  Fragments  Summary");
        foreach (var stack in stacks)
            output.Line($"{stack.StackId.PadRight(idWidth)}  {stack.PackageCount.ToString(System.Globalization.CultureInfo.InvariantCulture).PadRight(packageWidth)}  {stack.FragmentCount,9}  {stack.Summary ?? string.Empty}");
    }

    public static void StackDetails(CliOutput output, RegistryStackDetails stack)
    {
        output.Line($"Stack: {stack.StackId}");
        output.Line($"Name: {stack.Name}");
        if (!string.IsNullOrWhiteSpace(stack.Summary)) output.Line($"Summary: {stack.Summary}");
        output.Line($"Created (UTC): {stack.CreatedAtUtc.UtcDateTime:O}");
        output.Line($"Updated (UTC): {stack.UpdatedAtUtc.UtcDateTime:O}");
        output.Line("Packages:");
        foreach (var package in stack.Packages.OrderBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase))
            output.Line($"  {package.PackageId} {PackageRequirement(package)}");
        if (stack.Packages.Count == 0) output.Line("  none");
        output.Line("Fragments:");
        foreach (var fragment in stack.Fragments.OrderBy(item => item.FragmentId, StringComparer.OrdinalIgnoreCase))
            output.Line($"  {fragment.FragmentId} ({fragment.OwnerPackageId}, {(fragment.DefaultSelected ? "selected" : "off by default")})");
        if (stack.Fragments.Count == 0) output.Line("  none");
        output.Line("Required Inputs:");
        foreach (var input in stack.RequiredInputs.OrderBy(item => item.InputId, StringComparer.OrdinalIgnoreCase))
            output.Line($"  {input.InputId} ({(input.Required ? "required" : "optional")})");
        if (stack.RequiredInputs.Count == 0) output.Line("  none");
        output.Line($"Artifact SHA-256: {stack.Artifact.Sha256}");
        output.Line($"Artifact Size: {(stack.Artifact.Size is { } stackSize ? $"{stackSize} bytes" : "unknown")}");
    }

    public static int RegistryPackageChange(
        CliOutput output,
        RuntimeRegistryPackageChangeResult result,
        bool includePlan = true,
        bool writeData = true)
    {
        if (writeData) output.Data(CliJsonData.RegistryPackageChange(result));
        if (includePlan && result.PlanItems.Count > 0)
        {
            output.Info("Install plan:");
            foreach (var item in result.PlanItems.OrderBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase))
            {
                var action = item.CurrentVersion is null ? item.Version : $"{item.CurrentVersion} -> {item.Version}";
                output.Line($"  {item.PackageId} {action}");
            }
        }
        if (includePlan) CapabilityConsents(output, result.RequiredCapabilityConsents, approved: false);
        foreach (var warning in result.Warnings) output.Warning(warning);
        if (!result.Success)
        {
            RuntimePrerequisites(output, result.RuntimePrerequisites);
            var code = $"runtime.registry.{CliJsonData.RegistryErrorCode(result.ErrorCode)}";
            foreach (var error in result.Errors.DefaultIfEmpty(result.Message)) output.Error(error, code);
            return CliErrorMapper.FromRegistryCode(result.ErrorCode);
        }
        output.Success(result.Message);
        return CliExitCodes.Success;
    }

    public static int RegistryPackagePreflight(
        CliOutput output,
        CliPackageChangePreflight preflight,
        bool capabilitiesApproved,
        string? completionMessage)
    {
        output.Info("Package change preflight:");
        output.Line($"  package downloads: {Bytes(preflight.PackageDownloadBytes)}");
        output.Line($"  shared runtime downloads (full): {Bytes(preflight.FullRuntimeDownloadBytes)}");
        output.Line($"  shared runtime downloads (incremental on this Runtime): {Bytes(preflight.IncrementalRuntimeDownloadBytes)}");
        output.Line($"  shared runtime bytes already installed and healthy: {Bytes(preflight.ReusedRuntimeBytes)}");
        output.Line($"  total incremental download: {Bytes(preflight.IncrementalDownloadBytes)}");

        if (preflight.PlanExpectations.Count > 0)
        {
            output.Info("Registry plan fences:");
            foreach (var expectation in preflight.PlanExpectations)
            {
                output.Line(
                    $"  {expectation.RegistryOrigin} "
                    + $"({(expectation.IncludePrerelease ? "prerelease allowed" : "stable only")}): "
                    + expectation.ExpectedResolutionFingerprint);
            }
        }

        output.Info("Plan:");
        foreach (var item in preflight.Items)
        {
            var action = item.CurrentVersion is null
                ? item.Version ?? "-"
                : $"{item.CurrentVersion} -> {item.Version ?? "-"}";
            output.Line($"  {item.PackageId} {item.ActionKind}: {action}");
        }
        if (preflight.Items.Count == 0) output.Line("  no package changes");

        if (preflight.RuntimeDownloads.Count > 0)
        {
            output.Info("Shared execution runtimes:");
            foreach (var download in preflight.RuntimeDownloads)
            {
                var artifact = download.Resolution.Artifact;
                var identity = artifact.Identity;
                output.Line(
                    $"  {identity.RuntimeId} {identity.VendorVersion} "
                    + $"{identity.Rid} {identity.ModuleAbi} {DisplaySource(identity.SourceId)} "
                    + $"{DisplayChecksumAlgorithm(identity.VendorChecksumAlgorithm)}:{identity.VendorChecksumDigest}: "
                    + (download.AlreadyInstalled
                        ? $"0 incremental bytes ({DisplayBytes(download.FullBytes)} already installed and healthy)"
                        : $"{DisplayBytes(download.FullBytes)} incremental"));
                output.Line($"    support: {artifact.Support.State} ({artifact.Support.SupportLine})");
            }
        }

        foreach (var conflict in preflight.Conflicts)
        {
            output.Error(
                string.IsNullOrWhiteSpace(conflict.Message)
                    ? $"Package '{conflict.PackageId}' conflicts with the requested change."
                    : conflict.Message,
                conflict.ErrorCode);
        }
        foreach (var warning in preflight.Warnings) output.Warning(warning);
        foreach (var error in preflight.Errors) output.Error(error, "runtime.registry.conflict");
        CapabilityConsents(output, preflight.RequiredCapabilityConsents, capabilitiesApproved);

        if (!preflight.Success || preflight.HasConflicts)
        {
            return CliExitCodes.Conflict;
        }
        if (!string.IsNullOrWhiteSpace(completionMessage)) output.Info(completionMessage);
        return CliExitCodes.Success;
    }

    public static void PackageRuntimeStatus(
        CliOutput output,
        IReadOnlyList<CliPackageRuntimeStatus> runtimes)
    {
        output.Line("Execution runtimes:");
        if (runtimes.Count == 0)
        {
            output.Line("  none");
            return;
        }

        foreach (var status in runtimes)
        {
            var binding = status.Binding;
            output.Line($"  Target: {binding.Role}/{binding.Rid}");
            output.Line(
                $"    Requirement: {binding.Requirement.RuntimeId} {binding.Requirement.VersionRange} "
                + $"(ABI {binding.Requirement.ModuleAbi})");
            output.Line(
                $"    Binding: {binding.Runtime.RuntimeId} {binding.Runtime.VendorVersion} "
                + $"/ {binding.Runtime.Rid} / ABI {binding.Runtime.ModuleAbi} / {DisplaySource(binding.Runtime.SourceId)} "
                + $"/ {DisplayChecksumAlgorithm(binding.Runtime.VendorChecksumAlgorithm)}:{binding.Runtime.VendorChecksumDigest}");
            output.Line($"    Health: {status.InstalledRuntime?.Health.Status ?? "missing"}");
            output.Line($"    Support: {status.InstalledRuntime?.Support.State.ToString() ?? "unknown"}");
            output.Line($"    Package impact: {string.Join(", ", status.ImpactedPackageIds)}");
            output.Line($"    Action: {status.ActionMessage}");
            if (status.EnsureCommand is not null) output.Line($"      {status.EnsureCommand}");
            if (status.RepairCommand is not null) output.Line($"      {status.RepairCommand}");
            if (status.RebindPreflightCommand is not null) output.Line($"      preview: {status.RebindPreflightCommand}");
            if (status.RebindApplyCommand is not null) output.Line($"      apply: {status.RebindApplyCommand}");
            if (!string.IsNullOrWhiteSpace(status.InstalledRuntime?.Health.Message))
                output.Warning(status.InstalledRuntime.Health.Message);
        }
    }

    private static string DisplayBytes(long? bytes) => bytes is null ? "unknown size" : Bytes(bytes.Value);

    private static string DisplaySource(ConfiguredOfficialSourceId source) => source switch
    {
        ConfiguredOfficialSourceId.DotNet => "dotnet",
        ConfiguredOfficialSourceId.Node => "node",
        _ => source.ToString(),
    };

    private static string DisplayChecksumAlgorithm(RuntimeExecutionRuntimeChecksumAlgorithm algorithm) => algorithm switch
    {
        RuntimeExecutionRuntimeChecksumAlgorithm.Sha512 => "sha512",
        RuntimeExecutionRuntimeChecksumAlgorithm.Sha256 => "sha256",
        _ => algorithm.ToString(),
    };

    public static void CapabilityConsents(
        CliOutput output,
        IReadOnlyList<PackageCapabilityConsent> consents,
        bool approved)
    {
        if (consents.Count == 0)
        {
            return;
        }
        output.Info(approved
            ? "Approved sensitive capability fences:"
            : "Required sensitive capability consent:");
        foreach (var consent in consents.OrderBy(item => item.PackageId, StringComparer.Ordinal))
        {
            output.Line($"  {consent.PackageId} {consent.PackageVersion}");
            output.Line($"    manifest {consent.ManifestSha256}");
            output.Line($"    capabilities {string.Join(", ", consent.Capabilities)}");
        }
        if (!approved)
        {
            output.Info("Rerun the command with '--approve-sensitive-capabilities' after reviewing these exact fences.");
        }
    }

    public static int PackageOperation(CliOutput output, PackageOperationResult result)
    {
        output.Data(CliJsonData.PackageOperation(result));
        if (!result.Success) RuntimePrerequisites(output, result.RuntimePrerequisites);
        foreach (var warning in result.Warnings) output.Warning(warning);
        foreach (var error in result.Errors) output.Error(error, "runtime.package.operation_failed");
        if (!string.IsNullOrWhiteSpace(result.Message)) WriteOutcome(output, result.Success, result.Message);
        if (result.RequiresAppRestart) output.Warning("Restart Sunder to apply this package change.");
        return result.Success ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    public static int PackageValidation(CliOutput output, SunderPackageArchiveValidationResult result)
    {
        output.Data(CliJsonData.PackageValidation(result));
        foreach (var warning in result.Warnings) output.Warning(warning);
        foreach (var error in result.Errors) output.Error(error, "cli.package.invalid");
        if (result.Success) output.Success($"Package is valid: {result.Manifest!.Id} {result.Manifest.Version}");
        return result.Success ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    public static int StackValidation(CliOutput output, SunderStackArchiveValidationResult result)
    {
        output.Data(CliJsonData.StackValidation(result));
        foreach (var warning in result.Warnings) output.Warning(warning);
        foreach (var error in result.Errors) output.Error(error, "cli.stack.invalid");
        if (result.Success) output.Success($"Stack is valid: {result.Manifest!.StackId} ({result.Manifest.Name})");
        return result.Success ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    public static int PackagePublish(CliOutput output, RegistryPublishPackageResponse result)
    {
        output.Data(CliJsonData.PackagePublish(result));
        foreach (var warning in result.Warnings ?? []) output.Warning(warning);
        foreach (var error in result.Errors ?? []) output.Error(error, result.ErrorCode ?? "registry.package.publish_failed");
        if (!string.IsNullOrWhiteSpace(result.Message)) WriteOutcome(output, result.Success, result.Message);
        if (result.Success) return CliExitCodes.Success;
        if (result.Forbidden || result.ErrorCode == RegistryV1ErrorCodes.Forbidden) return CliExitCodes.Forbidden;
        return result.ErrorCode switch
        {
            RegistryV1ErrorCodes.Unauthorized => CliExitCodes.Authentication,
            RegistryV1ErrorCodes.NotFound => CliExitCodes.NotFound,
            RegistryV1ErrorCodes.Conflict or RegistryV1ErrorCodes.PackageVersionExists => CliExitCodes.Conflict,
            RegistryV1ErrorCodes.RateLimited => CliExitCodes.Unavailable,
            RegistryV1ErrorCodes.RequestTooLarge => CliExitCodes.Failure,
            _ => CliExitCodes.Failure,
        };
    }

    public static int StackPublish(CliOutput output, RegistryPublishStackResponse result)
    {
        output.Data(CliJsonData.StackPublish(result));
        foreach (var warning in result.Warnings ?? []) output.Warning(warning);
        foreach (var error in result.Errors ?? []) output.Error(error, "registry.stack.publish_failed");
        if (!string.IsNullOrWhiteSpace(result.Message)) WriteOutcome(output, result.Success, result.Message);
        if (result.Success && result.StackId is not null) output.Info($"Show link: sunder://stacks/{Uri.EscapeDataString(result.StackId)}");
        return result.Success
            ? CliExitCodes.Success
            : result.Forbidden
                ? CliExitCodes.Forbidden
                : result.NotFound ? CliExitCodes.NotFound : CliExitCodes.Failure;
    }

    public static int Management(CliOutput output, RegistryPackageManagementOperationResponse result)
    {
        output.Data(CliJsonData.Management(result.Success, result.Message, result.Errors ?? []));
        foreach (var error in result.Errors ?? []) output.Error(error, "registry.package.management_failed");
        if (!string.IsNullOrWhiteSpace(result.Message)) WriteOutcome(output, result.Success, result.Message);
        return result.Success
            ? CliExitCodes.Success
            : result.Forbidden
                ? CliExitCodes.Forbidden
                : result.NotFound ? CliExitCodes.NotFound : CliExitCodes.Failure;
    }

    public static int StackManagement(CliOutput output, RegistryStackManagementOperationResponse result)
    {
        output.Data(CliJsonData.Management(result.Success, result.Message, result.Errors ?? []));
        foreach (var error in result.Errors ?? []) output.Error(error, "registry.stack.management_failed");
        if (!string.IsNullOrWhiteSpace(result.Message)) WriteOutcome(output, result.Success, result.Message);
        return result.Success
            ? CliExitCodes.Success
            : result.Forbidden
                ? CliExitCodes.Forbidden
                : result.NotFound ? CliExitCodes.NotFound : CliExitCodes.Failure;
    }

    private static string PackageRequirement(RegistryStackPackageRequirement package)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(package.InstallTag)) parts.Add(package.InstallTag);
        if (!string.IsNullOrWhiteSpace(package.MinimumVersion)) parts.Add($">= {package.MinimumVersion}");
        if (!string.IsNullOrWhiteSpace(package.CreatedWithVersion)) parts.Add($"created with {package.CreatedWithVersion}");
        parts.Add(package.Required ? "required" : "optional");
        return string.Join(" - ", parts);
    }

    private static void WriteOutcome(CliOutput output, bool success, string message)
    {
        if (success) output.Success(message);
        else output.Error(message);
    }

    private static void RuntimePrerequisites(CliOutput output, RuntimePackageRuntimePrerequisitePlan? plan)
    {
        if (plan is null || plan.Satisfied) return;
        output.Info("Install the required runtime first in Settings > Runtimes, then re-preview and retry the package operation.");
        foreach (var requirement in plan.Requirements.Where(item => !item.Satisfied).Select(item => item.Requirement).Distinct())
            output.Line($"  sunder runtime install {requirement.RuntimeId} \"{requirement.VersionRange}\" {requirement.ModuleAbi} --yes");
        output.Info("Use your configured Registry; the upload or prerequisite preview is not package-install consent.");
    }

    private static string Bytes(long value)
        => $"{value} bytes ({FormatBytes(value)})";

    private static string FormatBytes(long bytes)
        => bytes switch
        {
            >= 1024L * 1024L * 1024L => $"{bytes / (1024d * 1024d * 1024d):0.##} GB",
            >= 1024L * 1024L => $"{bytes / (1024d * 1024d):0.##} MB",
            >= 1024L => $"{bytes / 1024d:0.##} KB",
            _ => $"{bytes} bytes",
        };
}
