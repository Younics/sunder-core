using System.Collections.ObjectModel;
using Sunder.Protocol;

namespace Sunder.App.ViewModels;

internal sealed class SettingsPackageSectionsViewModel(
    SettingsPackageSectionLoader sectionLoader,
    SettingsPackageSelectionCoordinator selectionCoordinator)
{
    private readonly Dictionary<string, PackageConfigurationSchemaDescriptor> _schemasByPackageId = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<SettingsSectionItemViewModel> PackageSections { get; } = [];

    public ObservableCollection<SettingsFieldSectionViewModel> SelectedPackageSections { get; } = [];

    public bool HasPackageSections => PackageSections.Count > 0;

    public async Task LoadSectionsAsync(CancellationToken cancellationToken)
    {
        var result = await sectionLoader.LoadAsync(cancellationToken);
        PackageSections.Clear();
        _schemasByPackageId.Clear();

        foreach (var schema in result.SchemasByPackageId)
        {
            _schemasByPackageId[schema.Key] = schema.Value;
        }

        foreach (var section in result.Sections)
        {
            PackageSections.Add(section);
        }
    }

    public SettingsSectionItemViewModel? FindSection(string packageId)
        => PackageSections.FirstOrDefault(section => string.Equals(section.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    public bool TryGetSchema(string packageId, out PackageConfigurationSchemaDescriptor? schema)
        => _schemasByPackageId.TryGetValue(packageId, out schema);

    public void ClearSelectedSections()
    {
        SelectedPackageSections.Clear();
    }

    public async Task<SettingsPackageSelectionResult> LoadSelectionAsync(
        string packageId,
        PackageConfigurationSchemaDescriptor? schema,
        CancellationToken cancellationToken)
        => await selectionCoordinator.LoadAsync(packageId, schema, cancellationToken);

    public void ApplySelectionResult(SettingsPackageSelectionResult result)
    {
        foreach (var section in result.PackageSections)
        {
            SelectedPackageSections.Add(section);
        }
    }
}
