using Sunder.App.Services;
using Sunder.Protocol;

namespace Sunder.App.ViewModels;

internal sealed class SettingsPackageSectionLoader(
    IRuntimeApiClient runtimeApiClient,
    PackageViewHostService packageViewHostService)
{
    public async Task<SettingsPackageSectionsLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        var schemas = await runtimeApiClient.GetConfigurationSchemasAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var sections = new List<SettingsSectionItemViewModel>();
        var schemasByPackageId = new Dictionary<string, PackageConfigurationSchemaDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var schema in schemas)
        {
            schemasByPackageId[schema.PackageId] = schema;
            sections.Add(new SettingsSectionItemViewModel(
                schema.PackageId,
                schema.PackageDisplayName,
                schema.Summary ?? $"Configure {schema.PackageDisplayName}.",
                true,
                schema.PackageId));
        }

        foreach (var settingsViewPackage in packageViewHostService.ListSettingsViewPackages())
        {
            if (schemasByPackageId.ContainsKey(settingsViewPackage.PackageId))
            {
                continue;
            }

            sections.Add(new SettingsSectionItemViewModel(
                settingsViewPackage.PackageId,
                settingsViewPackage.DisplayName,
                settingsViewPackage.Summary ?? $"Configure {settingsViewPackage.DisplayName}.",
                true,
                settingsViewPackage.PackageId));
        }

        var orderedSections = sections
            .OrderBy(section => section.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SettingsPackageSectionsLoadResult(orderedSections, schemasByPackageId);
    }
}

internal sealed record SettingsPackageSectionsLoadResult(
    IReadOnlyList<SettingsSectionItemViewModel> Sections,
    IReadOnlyDictionary<string, PackageConfigurationSchemaDescriptor> SchemasByPackageId);
