using Avalonia.Controls;
using Sunder.App.Services;
using Sunder.Protocol;

namespace Sunder.App.ViewModels;

internal sealed class SettingsPackageSelectionCoordinator(
    IRuntimeApiClient runtimeApiClient,
    PackageViewHostService packageViewHostService)
{
    public async Task<SettingsPackageSelectionResult> LoadAsync(
        string packageId,
        PackageConfigurationSchemaDescriptor? schema,
        CancellationToken cancellationToken)
    {
        if (packageViewHostService.HasSettingsView(packageId))
        {
            var hostedSettingsView = packageViewHostService.GetOrCreateSettingsView(packageId);
            return new SettingsPackageSelectionResult(
                hostedSettingsView,
                [],
                hostedSettingsView is null ? "Package settings view is unavailable." : string.Empty);
        }

        if (schema is null)
        {
            return new SettingsPackageSelectionResult(
                HostedSettingsView: null,
                PackageSections: [],
                StatusText: "This package does not provide configurable settings.");
        }

        var values = await runtimeApiClient.GetPackageConfigurationValuesAsync(packageId, cancellationToken);
        return new SettingsPackageSelectionResult(
            HostedSettingsView: null,
            PackageSections: SettingsPackageConfigurationFormFactory.Create(schema, values),
            StatusText: string.Empty);
    }
}

internal sealed record SettingsPackageSelectionResult(
    Control? HostedSettingsView,
    IReadOnlyList<SettingsFieldSectionViewModel> PackageSections,
    string StatusText);
