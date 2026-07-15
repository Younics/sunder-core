using System.Collections.ObjectModel;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed class SettingsPackageSectionsViewModel : ViewModelBase
{
    private readonly SettingsPackageSectionLoader _sectionLoader;
    private readonly SettingsPackageSelectionCoordinator _selectionCoordinator;
    private readonly Dictionary<string, PackageSettingsSchemaDescriptor> _schemasByPackageId = new(StringComparer.OrdinalIgnoreCase);

    internal SettingsPackageSectionsViewModel(
        SettingsPackageSectionLoader sectionLoader,
        SettingsPackageSelectionCoordinator selectionCoordinator)
    {
        _sectionLoader = sectionLoader;
        _selectionCoordinator = selectionCoordinator;
    }

    public ObservableCollection<SettingsSectionItemViewModel> PackageSections { get; } = [];

    public ObservableCollection<SettingsFieldSectionViewModel> SelectedPackageSections { get; } = [];

    public bool HasPackageSections => PackageSections.Count > 0;

    internal Task<SettingsPackageSectionsLoadResult> LoadSectionsAsync(CancellationToken cancellationToken)
        => _sectionLoader.LoadAsync(cancellationToken);

    internal void ApplySections(SettingsPackageSectionsLoadResult result)
    {
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

        OnPropertyChanged(nameof(HasPackageSections));
    }

    internal SettingsSectionItemViewModel? FindSection(string packageId)
        => PackageSections.FirstOrDefault(section => string.Equals(section.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    internal bool TryGetSchema(string packageId, out PackageSettingsSchemaDescriptor? schema)
        => _schemasByPackageId.TryGetValue(packageId, out schema);

    internal void ClearSelectedSections()
    {
        SelectedPackageSections.Clear();
    }

    internal async Task<SettingsPackageSelectionResult> LoadSelectionAsync(
        string packageId,
        PackageSettingsSchemaDescriptor? schema,
        CancellationToken cancellationToken)
        => await _selectionCoordinator.LoadAsync(packageId, schema, cancellationToken);

    internal void ApplySelectionResult(SettingsPackageSelectionResult result)
    {
        var sections = result is PackageSettingsFormSelection form ? form.Sections : [];
        foreach (var section in sections)
        {
            SelectedPackageSections.Add(section);
        }
    }
}
