using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

internal static class SettingsFieldViewModelFactory
{
    public static SettingsFieldViewModel Create(
        PackageSettingsFieldDescriptor field,
        string? value,
        bool hasStoredSecretValue)
    {
        return field.Kind switch
        {
            PackageSettingsFieldKind.Secret => new SecretSettingsFieldViewModel(
                field.Key,
                field.Label,
                field.Description,
                field.IsRequired,
                field.Placeholder,
                hasStoredSecretValue,
                null),
            PackageSettingsFieldKind.Boolean => new BooleanSettingsFieldViewModel(
                field.Key,
                field.Label,
                field.Description,
                field.IsRequired,
                bool.TryParse(value, out var parsedBoolean) && parsedBoolean),
            PackageSettingsFieldKind.Select => new SelectSettingsFieldViewModel(
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
