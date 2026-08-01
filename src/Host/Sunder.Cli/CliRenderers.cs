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
            output.Line($"  bundle {contract.ContractId} {contract.Version}");
        foreach (var contract in version.UsesContracts.OrderBy(item => item.ContractId, StringComparer.Ordinal))
            output.Line($"  uses {contract.ContractId} {contract.VersionRange} ({(contract.Required ? "required" : "optional")})");
        foreach (var provider in version.Providers.OrderBy(item => item.ProviderId, StringComparer.Ordinal))
            output.Line($"  provides {provider.ProviderId} -> {provider.ContractId} {provider.ContractVersion} ({provider.Role})");
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

    public static int RegistryPackageChange(CliOutput output, RuntimeRegistryPackageChangeResult result)
    {
        output.Data(result);
        if (result.PlanItems.Count > 0)
        {
            output.Info("Install plan:");
            foreach (var item in result.PlanItems.OrderBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase))
            {
                var action = item.CurrentVersion is null ? item.Version : $"{item.CurrentVersion} -> {item.Version}";
                output.Line($"  {item.PackageId} {action}");
            }
        }
        foreach (var warning in result.Warnings) output.Warning(warning);
        if (!result.Success)
        {
            foreach (var error in result.Errors.DefaultIfEmpty(result.Message)) output.Error(error);
            return CliErrorMapper.FromRegistryCode(result.ErrorCode);
        }
        output.Success(result.Message);
        return CliExitCodes.Success;
    }

    public static int PackageOperation(CliOutput output, PackageOperationResult result)
    {
        output.Data(result);
        foreach (var warning in result.Warnings) output.Warning(warning);
        foreach (var error in result.Errors) output.Error(error);
        if (!string.IsNullOrWhiteSpace(result.Message)) WriteOutcome(output, result.Success, result.Message);
        if (result.RequiresAppRestart) output.Warning("Restart Sunder to apply this package change.");
        return result.Success ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    public static int PackageValidation(CliOutput output, SunderPackageArchiveValidationResult result)
    {
        output.Data(new { result.Success, result.Manifest, result.Warnings, result.Errors });
        foreach (var warning in result.Warnings) output.Warning(warning);
        foreach (var error in result.Errors) output.Error(error);
        if (result.Success) output.Success($"Package is valid: {result.Manifest!.Id} {result.Manifest.Version}");
        return result.Success ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    public static int StackValidation(CliOutput output, SunderStackArchiveValidationResult result)
    {
        output.Data(new { result.Success, result.Manifest, result.Warnings, result.Errors });
        foreach (var warning in result.Warnings) output.Warning(warning);
        foreach (var error in result.Errors) output.Error(error);
        if (result.Success) output.Success($"Stack is valid: {result.Manifest!.StackId} ({result.Manifest.Name})");
        return result.Success ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    public static int PackagePublish(CliOutput output, RegistryPublishPackageResponse result)
    {
        output.Data(result);
        foreach (var warning in result.Warnings) output.Warning(warning);
        foreach (var error in result.Errors) output.Error(error);
        if (!string.IsNullOrWhiteSpace(result.Message)) WriteOutcome(output, result.Success, result.Message);
        return result.Success ? CliExitCodes.Success : result.Forbidden ? CliExitCodes.Forbidden : CliExitCodes.Failure;
    }

    public static int StackPublish(CliOutput output, RegistryPublishStackResponse result)
    {
        output.Data(result);
        foreach (var warning in result.Warnings) output.Warning(warning);
        foreach (var error in result.Errors) output.Error(error);
        if (!string.IsNullOrWhiteSpace(result.Message)) WriteOutcome(output, result.Success, result.Message);
        if (result.Success && result.StackId is not null) output.Info($"Show link: sunder://stacks/{Uri.EscapeDataString(result.StackId)}");
        return result.Success ? CliExitCodes.Success : result.Forbidden ? CliExitCodes.Forbidden : CliExitCodes.Failure;
    }

    public static int Management(CliOutput output, RegistryPackageManagementOperationResponse result)
    {
        output.Data(result);
        foreach (var error in result.Errors) output.Error(error);
        if (!string.IsNullOrWhiteSpace(result.Message)) WriteOutcome(output, result.Success, result.Message);
        return result.Success ? CliExitCodes.Success : result.Forbidden ? CliExitCodes.Forbidden : CliExitCodes.Failure;
    }

    public static int StackManagement(CliOutput output, RegistryStackManagementOperationResponse result)
    {
        output.Data(result);
        foreach (var error in result.Errors) output.Error(error);
        if (!string.IsNullOrWhiteSpace(result.Message)) WriteOutcome(output, result.Success, result.Message);
        return result.Success ? CliExitCodes.Success : result.Forbidden ? CliExitCodes.Forbidden : CliExitCodes.Failure;
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
}
