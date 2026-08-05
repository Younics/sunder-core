namespace Sunder.App.ViewModels;

internal static class PackageConfigurationFormSerializer
{
    public static IReadOnlyDictionary<string, string?> Serialize(IEnumerable<SettingsFieldSectionViewModel> sections)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in sections.SelectMany(section => section.Fields))
        {
            if (field is SecretSettingsFieldViewModel secretField)
            {
                if (!string.IsNullOrWhiteSpace(secretField.Value))
                {
                    values[field.Key] = secretField.Value;
                }

                continue;
            }

            values[field.Key] = field.GetPersistedValue();
        }

        return values;
    }
}
