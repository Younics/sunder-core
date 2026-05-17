using Sunder.Protocol;

namespace Sunder.App.ViewModels;

internal static class SettingsPackageConfigurationFormFactory
{
    public static IReadOnlyList<SettingsFieldSectionViewModel> Create(
        PackageConfigurationSchemaDescriptor schema,
        PackageConfigurationValuesResponse? values)
    {
        var valueMap = values?.Values ?? new Dictionary<string, string?>();
        var storedSecretKeys = values?.StoredSecretKeys ?? [];

        return schema.Sections
            .Select(section =>
            {
                var fieldViewModels = section.Fields
                    .Select(field => SettingsFieldViewModelFactory.Create(
                        field,
                        valueMap.TryGetValue(field.Key, out var currentValue) ? currentValue : field.DefaultValue,
                        storedSecretKeys.Contains(field.Key, StringComparer.OrdinalIgnoreCase)))
                    .ToArray();

                return new SettingsFieldSectionViewModel(section.Title, section.Description, fieldViewModels);
            })
            .ToArray();
    }
}
