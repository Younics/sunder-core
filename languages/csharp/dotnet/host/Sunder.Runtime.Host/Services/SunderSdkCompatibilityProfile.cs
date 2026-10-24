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
        SunderSdkCapabilities.PackageDevelopmentV1,
        SunderSdkCapabilities.PackageDevelopmentRevisionsV1,
        SunderSdkCapabilities.PackageDevelopmentReloadV1,
        SunderSdkCapabilities.PackageDevelopmentLogsV1,
        SunderSdkCapabilities.PackageDevelopmentLogQueriesV1,
        SunderSdkCapabilities.PackageDevelopmentViewsV1,
        SunderSdkCapabilities.PackageDevelopmentFeedbackV1,
        SunderSdkCapabilities.WorkerExecutionObservationsV1,
        SunderSdkCapabilities.PackageLogNavigationV1,
        SunderSdkCapabilities.AppStateUpdatesV1,
        SunderSdkCapabilities.AppActivationServicesV1,
        SunderSdkCapabilities.BackgroundServicesV1,
        SunderSdkCapabilities.RuntimeGenerationsV1,
        SunderSdkCapabilities.BackgroundProcessesV1,
        SunderSdkCapabilities.SettingsSchemaV1,
        SunderSdkCapabilities.SettingsV1,
        SunderSdkCapabilities.StorageV1,
        SunderSdkCapabilities.StorageKeyMigrationV1,
        SunderSdkCapabilities.RoleLocalWorkspaceV1,
        SunderSdkCapabilities.DockerEnvironmentV1,
        SunderSdkCapabilities.SecretsV1,
        SunderSdkCapabilities.LoggingV1,
        SunderSdkCapabilities.NotificationsV1,
        SunderSdkCapabilities.ShellViewV1,
        SunderSdkCapabilities.ViewNavigationPreparationV1,
        SunderSdkCapabilities.RpcV1,
        SunderSdkCapabilities.RpcCallOwnershipV1,
        SunderSdkCapabilities.RpcExtendedDeadlineV1,
        SunderSdkCapabilities.StacksV1,
        SunderSdkCapabilities.StacksRpcV1,
        SunderSdkCapabilities.CallbacksV1,
        SunderSdkCapabilities.AuthV1,
        SunderSdkCapabilities.ThemingV1,
        SunderPackageFormat.ExecutionRuntimeV1Capability,
        SunderWorkerProtocol.V1Capability,
    };
    private static readonly IReadOnlyDictionary<string, bool> WorkerV1Capabilities =
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [SunderSdkCapabilities.Baseline11V1] = true,
            [SunderSdkCapabilities.CoreV1] = true,
            [SunderSdkCapabilities.PackagingV1] = true,
            [SunderSdkCapabilities.ContributionsV1] = true,
            [SunderSdkCapabilities.ViewsV1] = false,
            [SunderSdkCapabilities.SettingsViewsV1] = false,
            [SunderSdkCapabilities.SettingsNavigationV1] = false,
            [SunderSdkCapabilities.PackageDevelopmentV1] = false,
            [SunderSdkCapabilities.PackageDevelopmentRevisionsV1] = false,
            [SunderSdkCapabilities.PackageDevelopmentReloadV1] = false,
            [SunderSdkCapabilities.PackageDevelopmentLogsV1] = false,
            [SunderSdkCapabilities.PackageDevelopmentLogQueriesV1] = false,
            [SunderSdkCapabilities.PackageDevelopmentViewsV1] = false,
            [SunderSdkCapabilities.PackageDevelopmentFeedbackV1] = false,
            [SunderSdkCapabilities.WorkerExecutionObservationsV1] = false,
            [SunderSdkCapabilities.PackageLogNavigationV1] = false,
            [SunderSdkCapabilities.AppStateUpdatesV1] = false,
            [SunderSdkCapabilities.AppActivationServicesV1] = false,
            [SunderSdkCapabilities.BackgroundServicesV1] = true,
            [SunderSdkCapabilities.RuntimeGenerationsV1] = true,
            [SunderSdkCapabilities.BackgroundProcessesV1] = false,
            [SunderSdkCapabilities.SettingsSchemaV1] = true,
            [SunderSdkCapabilities.SettingsV1] = true,
            [SunderSdkCapabilities.StorageV1] = true,
            [SunderSdkCapabilities.StorageKeyMigrationV1] = true,
            [SunderSdkCapabilities.RoleLocalWorkspaceV1] = true,
            [SunderSdkCapabilities.DockerEnvironmentV1] = true,
            [SunderSdkCapabilities.SecretsV1] = true,
            [SunderSdkCapabilities.LoggingV1] = true,
            [SunderSdkCapabilities.NotificationsV1] = false,
            [SunderSdkCapabilities.ShellViewV1] = false,
            [SunderSdkCapabilities.ViewNavigationPreparationV1] = false,
            [SunderSdkCapabilities.RpcV1] = true,
            [SunderSdkCapabilities.RpcCallOwnershipV1] = true,
            [SunderSdkCapabilities.RpcExtendedDeadlineV1] = true,
            [SunderSdkCapabilities.StacksV1] = true,
            [SunderSdkCapabilities.StacksRpcV1] = true,
            [SunderSdkCapabilities.CallbacksV1] = true,
            [SunderSdkCapabilities.AuthV1] = true,
            [SunderSdkCapabilities.ThemingV1] = false,
            [SunderPackageFormat.ExecutionRuntimeV1Capability] = true,
            [SunderWorkerProtocol.V1Capability] = true,
        };

    internal static IReadOnlyDictionary<string, bool> WorkerV1CapabilityClassification
        => WorkerV1Capabilities;

    public static IReadOnlyList<string> Validate(
        string? packageId,
        SunderPackageTargetKey targetKey,
        SunderPackageTargetManifest target)
        => Validate(
            packageId,
            targetKey,
            target.Kind,
            target.SdkVersion,
            target.RequiredHostCapabilities);

    private static IReadOnlyList<string> Validate(
        string? packageId,
        SunderPackageTargetKey targetKey,
        string? targetKind,
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
            else if ((capability is SunderSdkCapabilities.WorkerExecutionObservationsV1 or SunderSdkCapabilities.PackageDevelopmentRevisionsV1 or SunderSdkCapabilities.PackageDevelopmentReloadV1 or SunderSdkCapabilities.PackageDevelopmentLogsV1 or SunderSdkCapabilities.PackageDevelopmentLogQueriesV1 or SunderSdkCapabilities.PackageDevelopmentViewsV1 or SunderSdkCapabilities.PackageDevelopmentFeedbackV1 or SunderSdkCapabilities.AppActivationServicesV1)
                && (targetKey.Role != SunderPackageFormat.AppHostRole || targetKind != SunderPackageFormat.AvaloniaTargetKind)
                && !(targetKey.Role == SunderPackageFormat.RuntimeHostRole && targetKind == SunderPackageFormat.WorkerTargetKind
                    && requiredHostCapabilities?.Contains(SunderWorkerProtocol.V1Capability, StringComparer.Ordinal) == true))
            {
                errors.Add($"Package '{packageLabel}' target '{targetKey}' requires Host capability '{capability}', but it is implemented only by the managed App.");
            }
        }

        var usesWorkerV1 = requiredHostCapabilities?.Contains(
            SunderWorkerProtocol.V1Capability,
            StringComparer.Ordinal) == true;
        var isRuntimeWorker = string.Equals(
                                  targetKey.Role,
                                  SunderPackageFormat.RuntimeHostRole,
                                  StringComparison.Ordinal)
                              && targetKind is SunderPackageFormat.WorkerTargetKind;
        if (usesWorkerV1 && !isRuntimeWorker)
        {
            errors.Add(
                $"Package '{packageLabel}' target '{targetKey}' requires Host capability '{SunderWorkerProtocol.V1Capability}', but that capability is valid only for Runtime worker targets.");
        }
        else if (usesWorkerV1)
        {
            foreach (var capability in (requiredHostCapabilities ?? [])
                         .Where(static capability => !string.IsNullOrWhiteSpace(capability))
                         .Select(static capability => capability!)
                         .Where(SupportedCapabilities.Contains))
            {
                if (!WorkerV1Capabilities.TryGetValue(capability, out var supported)
                    || !supported)
                {
                    errors.Add(
                        $"Package '{packageLabel}' target '{targetKey}' requires Host capability '{capability}', but Sunder Worker Protocol V1 does not implement that capability, so the worker process cannot be launched.");
                }
            }
        }

        return errors;
    }
}
