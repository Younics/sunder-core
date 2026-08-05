using Avalonia.Controls;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

internal sealed class SettingsPackageSelectionCoordinator(
    IRuntimePackageSettingsClient runtimeApiClient,
    PackageViewHostService packageViewHostService)
{
    public async Task<SettingsPackageSelectionResult> LoadAsync(
        string packageId,
        PackageSettingsSchemaDescriptor? schema,
        bool requireHostedViewReplacement,
        CancellationToken cancellationToken)
    {
        if (packageViewHostService.HasSettingsView(packageId))
        {
            var navigationTarget = packageViewHostService.GetSettingsViewForNavigation(
                packageId,
                requireHostedViewReplacement);
            var hostedSettingsView = packageViewHostService.CreateHostedViewBoundary(
                packageId,
                $"settings:{packageId}",
                navigationTarget?.View);
            return hostedSettingsView is null
                ? new UnavailablePackageSettingsSelection("Package settings view is unavailable.")
                : new HostedPackageSettingsSelection(hostedSettingsView, navigationTarget!.Candidate);
        }

        if (schema is null)
        {
            return new UnavailablePackageSettingsSelection("This package does not provide configurable settings.");
        }

        var values = await runtimeApiClient.GetPackageSettingsValuesAsync(packageId, cancellationToken);
        return new PackageSettingsFormSelection(SettingsPackageConfigurationFormFactory.Create(schema, values));
    }
}

internal abstract record SettingsPackageSelectionResult(string StatusText);

internal sealed record HostedPackageSettingsSelection(
    Control View,
    AppPackageSettingsViewCandidate? Candidate)
    : SettingsPackageSelectionResult(string.Empty), IDisposable
{
    public bool PromoteCandidate() => Candidate?.Promote() ?? true;

    public void Dispose() => Candidate?.Dispose();
}

internal sealed record PackageSettingsFormSelection(IReadOnlyList<SettingsFieldSectionViewModel> Sections)
    : SettingsPackageSelectionResult(string.Empty);

internal sealed record UnavailablePackageSettingsSelection(string Message)
    : SettingsPackageSelectionResult(Message);
