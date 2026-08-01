using Sunder.Package.Format;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal static class SunderSdkCompatibilityProfile
{
    private const string SupportedSdkRange = ">=1.1.0 <1.2.0";
    private static readonly HashSet<string> SupportedCapabilities = new(StringComparer.Ordinal)
    {
        SunderSdkCapabilities.Baseline11V1,
        SunderSdkCapabilities.CoreV1,
        SunderSdkCapabilities.PackagingV1,
        SunderSdkCapabilities.ContributionsV1,
        SunderSdkCapabilities.ViewsV1,
        SunderSdkCapabilities.SettingsViewsV1,
        SunderSdkCapabilities.SettingsNavigationV1,
        SunderSdkCapabilities.BackgroundServicesV1,
        SunderSdkCapabilities.RuntimeGenerationsV1,
        SunderSdkCapabilities.BackgroundProcessesV1,
        SunderSdkCapabilities.SettingsSchemaV1,
        SunderSdkCapabilities.SettingsV1,
        SunderSdkCapabilities.StorageV1,
        SunderSdkCapabilities.StorageKeyMigrationV1,
        SunderSdkCapabilities.RoleLocalWorkspaceV1,
        SunderSdkCapabilities.SecretsV1,
        SunderSdkCapabilities.LoggingV1,
        SunderSdkCapabilities.NotificationsV1,
        SunderSdkCapabilities.ShellViewV1,
        SunderSdkCapabilities.ViewNavigationPreparationV1,
        SunderSdkCapabilities.RuntimeOperationsV1,
        SunderSdkCapabilities.RuntimeInvocationErrorsV1,
        SunderSdkCapabilities.RpcV1,
        SunderSdkCapabilities.StacksV1,
        SunderSdkCapabilities.StacksRpcV1,
        SunderSdkCapabilities.CallbacksV1,
        SunderSdkCapabilities.AuthV1,
        SunderSdkCapabilities.ThemingV1,
    };

    public static IReadOnlyList<string> Validate(
        string? packageId,
        SunderPackageTargetKey targetKey,
        SunderPackageTargetManifest target)
        => Validate(
            packageId,
            targetKey,
            target.SdkVersion,
            target.RequiredHostCapabilities);

    private static IReadOnlyList<string> Validate(
        string? packageId,
        SunderPackageTargetKey targetKey,
        string? sdkPackageVersion,
        IReadOnlyList<string?>? requiredHostCapabilities)
    {
        var errors = new List<string>();
        var packageLabel = packageId ?? "unknown";
        if (!SemanticVersion.TryParse(sdkPackageVersion, out _)
            || !PackageVersionRange.IsSatisfiedBy(sdkPackageVersion!, SupportedSdkRange))
        {
            errors.Add($"Package '{packageLabel}' target '{targetKey}' was built with Sunder.Sdk '{sdkPackageVersion ?? "unknown"}', but this Host requires {SupportedSdkRange}.");
        }

        if (requiredHostCapabilities?.Contains(SunderSdkCapabilities.Baseline11V1, StringComparer.Ordinal) != true)
        {
            errors.Add($"Package '{packageLabel}' target '{targetKey}' does not declare the Sunder SDK 1.1 baseline capability '{SunderSdkCapabilities.Baseline11V1}'.");
        }

        foreach (var capability in requiredHostCapabilities ?? [])
        {
            if (string.IsNullOrWhiteSpace(capability))
            {
                errors.Add($"Package '{packageLabel}' target '{targetKey}' declares an empty Host capability requirement.");
                continue;
            }

            if (!SupportedCapabilities.Contains(capability))
            {
                errors.Add($"Package '{packageLabel}' target '{targetKey}' requires Host capability '{capability}', but this Host does not support it.");
            }
        }

        return errors;
    }
}
