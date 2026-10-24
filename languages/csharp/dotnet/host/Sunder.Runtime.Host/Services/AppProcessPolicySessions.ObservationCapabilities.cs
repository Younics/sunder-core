using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Compatibility;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class AppProcessPolicySessions
{
    private static void RequireObservationCapabilities(RuntimeAppPackageLoadPermitRequest request,
        RuntimePackageSource source, SunderPackageTargetManifest? selected)
    {
        // An untracked legacy request cannot omit its target to hide a newer
        // managed App requirement. Tracked requests have already matched the
        // exact Host-selected manifest target, not merely a supplied descriptor.
        IEnumerable<SunderPackageTargetManifest?> targets = selected is null ? source.Manifest?.Targets ?? [] : [selected];
        var required = targets.Where(target => target?.Role == SunderPackageFormat.AppHostRole)
            .SelectMany(target => target!.RequiredHostCapabilities ?? [])
            .Where(capability => capability is SunderSdkCapabilities.WorkerExecutionObservationsV1
                or SunderSdkCapabilities.PackageDevelopmentRevisionsV1 or SunderSdkCapabilities.PackageDevelopmentReloadV1 or SunderSdkCapabilities.PackageDevelopmentLogsV1 or SunderSdkCapabilities.PackageDevelopmentLogQueriesV1 or SunderSdkCapabilities.PackageDevelopmentViewsV1 or SunderSdkCapabilities.PackageDevelopmentFeedbackV1 or SunderSdkCapabilities.AppActivationServicesV1)
            .Select(capability => capability!).Distinct(StringComparer.Ordinal).ToArray();
        var implemented = request.AppImplementedCapabilities;
        if (implemented is not null && (implemented.Count > 128
            || implemented.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128)
            || implemented.Distinct(StringComparer.Ordinal).Count() != implemented.Count))
            throw new RuntimeValidationException("The App implemented-capability metadata is invalid.");
        if (required.Length == 0) return;
        if (selected?.Kind != SunderPackageFormat.AvaloniaTargetKind || request.AppActivationId is null
            || implemented is null || required.Any(capability => !implemented.Contains(capability, StringComparer.Ordinal)))
            throw new RuntimeValidationException("Update the App to implement the selected managed App capabilities: "
                + string.Join(", ", required) + ".");
    }
}
