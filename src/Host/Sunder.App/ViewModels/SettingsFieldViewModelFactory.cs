using Sunder.Protocol;

namespace Sunder.App.ViewModels;

internal static class SettingsFieldViewModelFactory
{
    public static SettingsFieldViewModel Create(
        PackageConfigurationFieldDescriptor field,
        string? value,
        bool hasStoredSecretValue)
    {
        return field.Kind switch
        {
            PackageConfigurationFieldKind.Secret => new SecretSettingsFieldViewModel(
                field.Key,
                field.Label,
                field.Description,
                field.IsRequired,
                field.Placeholder,
                hasStoredSecretValue,
                null),
            PackageConfigurationFieldKind.Boolean => new BooleanSettingsFieldViewModel(
                field.Key,
                field.Label,
                field.Description,
                field.IsRequired,
                bool.TryParse(value, out var parsedBoolean) && parsedBoolean),
            PackageConfigurationFieldKind.Select => new SelectSettingsFieldViewModel(
                field.Key,
                field.Label,
                field.Description,
                field.IsRequired,
                field.Options.Select(option => new SettingsOptionItem(option.Value, option.Label)).ToArray(),
                value),
            _ => new TextSettingsFieldViewModel(
                field.Key,
                field.Label,
                field.Description,
                field.IsRequired,
                field.Placeholder,
                value),
        };
    }
}
