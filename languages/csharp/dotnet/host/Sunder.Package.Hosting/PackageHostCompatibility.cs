using Sunder.Package.Format;
using Sunder.Sdk.Compatibility;
using System.Collections.Frozen;
using System.Reflection;

namespace Sunder.Package.Hosting;

public static class PackageHostCompatibility
{
    private const string WorkerProtocolV1 = "worker-protocol.v1";
    private static readonly HashSet<string> SupportedCapabilities = new(StringComparer.Ordinal)
    {
        SunderSdkCapabilities.DevelopmentToolchainV1,
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
        SunderSdkCapabilities.RpcConsumerRequirementsV1,
        SunderSdkCapabilities.RpcCallOwnershipV1,
        SunderSdkCapabilities.RpcExtendedDeadlineV1,
        SunderSdkCapabilities.StacksV1,
        SunderSdkCapabilities.StacksRpcV1,
        SunderSdkCapabilities.CallbacksV1,
        SunderSdkCapabilities.AuthV1,
        SunderSdkCapabilities.ThemingV1,
        SunderPackageFormat.ExecutionRuntimeV1Capability,
        WorkerProtocolV1,
    };
    private static readonly IReadOnlyDictionary<string, bool> WorkerV1Capabilities =
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [SunderSdkCapabilities.DevelopmentToolchainV1] = true,
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
            [SunderSdkCapabilities.RpcConsumerRequirementsV1] = true,
            [SunderSdkCapabilities.RpcCallOwnershipV1] = true,
            [SunderSdkCapabilities.RpcExtendedDeadlineV1] = true,
            [SunderSdkCapabilities.StacksV1] = true,
            [SunderSdkCapabilities.StacksRpcV1] = true,
            [SunderSdkCapabilities.CallbacksV1] = true,
            [SunderSdkCapabilities.AuthV1] = true,
            [SunderSdkCapabilities.ThemingV1] = false,
            [SunderPackageFormat.ExecutionRuntimeV1Capability] = true,
            [WorkerProtocolV1] = true,
        };

    public static IReadOnlyDictionary<string, bool> WorkerV1CapabilityClassification
        => WorkerV1Capabilities;

    private static string Metadata(string name) => typeof(PackageHostCompatibility).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(value => value.Key == name).Value!;

    public static IReadOnlyList<PackageImplementationProfile> SupportedProfiles { get; } = Array.AsReadOnly<PackageImplementationProfile>(
    [
        new("runtime", "worker", new[] { 1 }.ToFrozenSet(),
            WorkerV1Capabilities.Where(capability => capability.Value).Select(capability => capability.Key).ToFrozenSet(StringComparer.Ordinal),
            Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal)),
        new("app", "avalonia", new[] { 1 }.ToFrozenSet(),
            SupportedCapabilities.Except(new[] { WorkerProtocolV1, SunderPackageFormat.ExecutionRuntimeV1Capability,
                SunderSdkCapabilities.BackgroundServicesV1, SunderSdkCapabilities.RuntimeGenerationsV1, SunderSdkCapabilities.DockerEnvironmentV1 }, StringComparer.Ordinal)
                .Append("app-bridge.v1").ToFrozenSet(StringComparer.Ordinal),
            new[] { Metadata("AppTargetFramework") }.ToFrozenSet(StringComparer.Ordinal), Metadata("AppAvaloniaVersion")),
        new("app", "web", new[] { 1 }.ToFrozenSet(),
            new[] { "core.v1", "views.v1", "web-bridge.v1", "rpc.v1", "rpc.consumer-requirements.v1", "rpc.call-ownership.v1", "rpc.extended-deadline.v1" }.ToFrozenSet(StringComparer.Ordinal),
            Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal)),
    ]);

    public static IReadOnlyList<string> Validate(
        string? packageId,
        SunderPackageTargetKey targetKey,
        SunderPackageTargetManifest target)
        => Validate(packageId, targetKey, target, SupportedProfiles);

    public static IReadOnlyList<string> Validate(string? packageId, SunderPackageTargetKey targetKey,
        SunderPackageTargetManifest target, IReadOnlyList<PackageImplementationProfile> profiles)
    {
        IReadOnlyList<string>? closest = null;
        foreach (var profile in profiles.Where(profile => profile.Role == targetKey.Role && profile.Kind == target.Kind))
        {
            var errors = profile.Validate(target);
            if (errors.Count == 0) return errors;
            if (closest is null || errors.Count < closest.Count) closest = errors;
        }
        return closest ?? [$"Package '{packageId ?? "unknown"}' target '{targetKey}' has no supported SDK/protocol profile."];
    }

}
