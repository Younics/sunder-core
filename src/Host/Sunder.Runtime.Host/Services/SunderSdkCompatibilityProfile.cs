using Sunder.Package.Format;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal static class SunderSdkCompatibilityProfile
{
    private const string SupportedSdkRange = ">=1.1.0 <1.2.0";
    private static readonly HashSet<int> SupportedApiVersions = [SunderSdkApiVersions.V1];

    private static readonly HashSet<string> SupportedCapabilities = new(StringComparer.Ordinal)
    {
        SunderSdkCapabilities.Baseline11V1,
        SunderSdkCapabilities.CoreV1,
        SunderSdkCapabilities.PackagingV1,
        SunderSdkCapabilities.ContributionsV1,
        SunderSdkCapabilities.ViewsV1,
        SunderSdkCapabilities.SettingsViewsV1,
        SunderSdkCapabilities.SettingsNavigationV1,
        SunderSdkCapabilities.WorkspacesV1,
        SunderSdkCapabilities.BackgroundServicesV1,
        SunderSdkCapabilities.BackgroundProcessesV1,
        SunderSdkCapabilities.ExtensionsV1,
        SunderSdkCapabilities.ExtensionChangesV1,
        SunderSdkCapabilities.ConfigurationSchemaV1,
        SunderSdkCapabilities.SettingsV1,
        SunderSdkCapabilities.StorageV1,
        SunderSdkCapabilities.RoleLocalWorkspaceV1,
        SunderSdkCapabilities.SecretsV1,
        SunderSdkCapabilities.LoggingV1,
        SunderSdkCapabilities.NotificationsV1,
        SunderSdkCapabilities.ShellViewV1,
        SunderSdkCapabilities.InstalledPackageSessionsV1,
        SunderSdkCapabilities.DevelopmentPackageSessionsV1,
        SunderSdkCapabilities.RuntimeOperationsV1,
        SunderSdkCapabilities.StacksV1,
        SunderSdkCapabilities.StackContributionsV1,
        SunderSdkCapabilities.CallbacksV1,
        SunderSdkCapabilities.AuthV1,
        SunderSdkCapabilities.ThemingV1,
    };

    public static IReadOnlyList<string> Validate(SunderPackageManifest manifest)
    {
        return Validate(
            manifest.Id,
            manifest.SdkApiVersion,
            manifest.SdkPackageVersion,
            manifest.RequiredSdkCapabilities);
    }

    private static IReadOnlyList<string> Validate(
        string? packageId,
        int? requiredSdkApiVersion,
        string? sdkPackageVersion,
        IReadOnlyList<string>? requiredSdkCapabilities)
    {
        var errors = new List<string>();
        var packageLabel = packageId ?? "unknown";
        if (requiredSdkApiVersion is null)
        {
            errors.Add($"Package '{packageLabel}' must declare SDK API version {SunderSdkApiVersions.V1}.");
        }
        else if (!SupportedApiVersions.Contains(requiredSdkApiVersion.Value))
        {
            errors.Add($"Package '{packageLabel}' requires SDK API version {requiredSdkApiVersion}, but this Sunder Host supports {string.Join(", ", SupportedApiVersions.Order())}.");
        }

        if (!SemanticVersion.TryParse(sdkPackageVersion, out _)
            || !PackageVersionRange.IsSatisfiedBy(sdkPackageVersion!, SupportedSdkRange))
        {
            errors.Add($"Package '{packageLabel}' was built with Sunder.Sdk '{sdkPackageVersion ?? "unknown"}', but this Host requires {SupportedSdkRange}. Rebuild all packages against the coordinated Sunder 1.1 SDK; 1.0 and 1.1 packages cannot be mixed.");
        }

        if (requiredSdkCapabilities?.Contains(SunderSdkCapabilities.Baseline11V1, StringComparer.Ordinal) != true)
        {
            errors.Add($"Package '{packageLabel}' does not declare the Sunder SDK 1.1 baseline capability '{SunderSdkCapabilities.Baseline11V1}'. Rebuild it with Sunder.Package.Build 1.1.x.");
        }

        foreach (var capability in requiredSdkCapabilities ?? [])
        {
            if (string.IsNullOrWhiteSpace(capability))
            {
                errors.Add($"Package '{packageLabel}' declares an empty SDK capability requirement.");
                continue;
            }

            if (!SupportedCapabilities.Contains(capability))
            {
                errors.Add($"Package '{packageLabel}' requires SDK capability '{capability}', but this Sunder Host does not support it.");
            }
        }

        return errors;
    }
}
