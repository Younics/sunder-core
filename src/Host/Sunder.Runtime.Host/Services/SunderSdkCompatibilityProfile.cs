using Sunder.PackageManagement;
using Sunder.Sdk.Compatibility;

namespace Sunder.Runtime.Host.Services;

internal static class SunderSdkCompatibilityProfile
{
    private static readonly HashSet<int> SupportedApiVersions = [SunderSdkApiVersions.V1];

    private static readonly HashSet<string> SupportedCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
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
        SunderSdkCapabilities.ConfigurationValuesV1,
        SunderSdkCapabilities.StorageV1,
        SunderSdkCapabilities.SecretsV1,
        SunderSdkCapabilities.LoggingV1,
        SunderSdkCapabilities.NotificationsV1,
        SunderSdkCapabilities.ShellViewV1,
        SunderSdkCapabilities.PackageSessionsV1,
        SunderSdkCapabilities.CallbacksV1,
        SunderSdkCapabilities.AuthV1,
        SunderSdkCapabilities.ThemingV1,
    };

    public static IReadOnlyList<string> Validate(DevPackageManifest manifest)
    {
        return Validate(
            manifest.Id,
            manifest.SdkApiVersion,
            manifest.RequiredSdkCapabilities);
    }

    public static IReadOnlyList<string> Validate(SunderPackageManifest manifest)
    {
        return Validate(
            manifest.Id,
            manifest.SdkApiVersion,
            manifest.RequiredSdkCapabilities);
    }

    private static IReadOnlyList<string> Validate(
        string? packageId,
        int? requiredSdkApiVersion,
        IReadOnlyList<string>? requiredSdkCapabilities)
    {
        var errors = new List<string>();
        var packageLabel = packageId ?? "unknown";
        var sdkApiVersion = requiredSdkApiVersion ?? SunderSdkApiVersions.V1;
        if (!SupportedApiVersions.Contains(sdkApiVersion))
        {
            errors.Add($"Package '{packageLabel}' requires SDK API version {sdkApiVersion}, but this Sunder Host supports {string.Join(", ", SupportedApiVersions.Order())}.");
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
