using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Sdk.Abstractions;
using CanonicalSettingsField = Sunder.Sdk.Settings.PackageSettingsField;
using CanonicalSettingsFieldKind = Sunder.Sdk.Settings.PackageSettingsFieldKind;
using StoredPackageSettings = Sunder.Runtime.Host.Infrastructure.Storage.PackageSettings;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSettingsService
{
    public IReadOnlyList<PackageSettingsSchemaDescriptor> GetSchemas(
        IReadOnlyList<ActiveLoadedPackage> loadedPackages)
        => loadedPackages
            .Where(package => package.SettingsSchema is not null)
            .Select(package => package.SettingsSchema!)
            .OrderBy(schema => schema.PackageDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<PackageSettingsValuesResponse> GetValuesAsync(
        ActiveLoadedPackage loadedPackage,
        CancellationToken cancellationToken = default)
    {
        var fields = GetProtocolFields(loadedPackage);
        var storedValues = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in fields.Where(field => field.Kind != Sunder.Runtime.Contracts.PackageSettingsFieldKind.Secret))
        {
            var storedValue = await GetSettings(loadedPackage).GetStoredValueAsync(field.Key, cancellationToken)
                .ConfigureAwait(false);
            if (storedValue is not null)
            {
                storedValues[field.Key] = storedValue;
            }
        }

        var storedSecretKeys = (await loadedPackage.SecretsStore.ListKeysAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        return new PackageSettingsValuesResponse(
            loadedPackage.Descriptor.PackageId,
            storedValues,
            fields
                .Where(field => field.Kind == Sunder.Runtime.Contracts.PackageSettingsFieldKind.Secret && storedSecretKeys.Contains(field.Key))
                .Select(field => field.Key)
                .ToArray());
    }

    public async Task SaveValuesAsync(
        ActiveLoadedPackage loadedPackage,
        UpdatePackageSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.Mode))
        {
            throw new RuntimeValidationException("The package settings update mode is invalid.");
        }

        var fields = GetCanonicalFields(loadedPackage);
        var fieldsByKey = fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        var currentSettings = new Dictionary<string, string>(StringComparer.Ordinal);
        var currentSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (field.Kind == CanonicalSettingsFieldKind.Secret)
            {
                if (await loadedPackage.SecretsStore.GetSecretAsync(field.Key, cancellationToken).ConfigureAwait(false) is { } secret)
                {
                    currentSecrets[field.Key] = secret;
                }
            }
            else if (await GetSettings(loadedPackage).GetStoredValueAsync(field.Key, cancellationToken).ConfigureAwait(false) is { } value)
            {
                currentSettings[field.Key] = value;
            }
        }

        var desiredSettings = request.Mode == PackageSettingsUpdateMode.Patch
            ? new Dictionary<string, string>(currentSettings, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var desiredSecrets = new Dictionary<string, string>(currentSecrets, StringComparer.Ordinal);
        foreach (var pair in request.Values)
        {
            if (!fieldsByKey.TryGetValue(pair.Key, out var field))
            {
                throw new RuntimeValidationException(
                    $"Setting '{pair.Key}' is not declared by package '{loadedPackage.Descriptor.PackageId}'.");
            }

            ValidateValue(field, pair.Value);
            var target = field.Kind == CanonicalSettingsFieldKind.Secret ? desiredSecrets : desiredSettings;
            if (pair.Value is null)
            {
                target.Remove(pair.Key);
            }
            else
            {
                target[pair.Key] = pair.Value;
            }
        }

        if (request.Mode == PackageSettingsUpdateMode.Replace)
        {
            foreach (var field in fields)
            {
                var storedValue = field.Kind == CanonicalSettingsFieldKind.Secret
                    ? desiredSecrets.GetValueOrDefault(field.Key)
                    : desiredSettings.GetValueOrDefault(field.Key);
                ValidateValue(field, storedValue);
            }
        }

        var settingsDocument = GetSettings(loadedPackage) as IPackageSettingsDocument
            ?? throw new InvalidOperationException("The Runtime package settings store does not support atomic document replacement.");
        await settingsDocument.ReplaceValuesAsync(desiredSettings, cancellationToken).ConfigureAwait(false);
        try
        {
            await loadedPackage.SecretsStore.ReplaceValuesAsync(desiredSecrets, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await settingsDocument.ReplaceValuesAsync(currentSettings, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                throw new RuntimeUnavailableException(
                    "The package settings update failed and its previous settings document could not be restored.",
                    rollbackException);
            }
            throw;
        }
    }

    public async Task<PackageSettingValueResponse> GetValueAsync(
        ActiveLoadedPackage loadedPackage,
        string key,
        CancellationToken cancellationToken = default)
    {
        var field = GetCanonicalField(loadedPackage, key);
        if (field.Kind == CanonicalSettingsFieldKind.Secret)
        {
            var stored = await loadedPackage.SecretsStore.GetSecretAsync(key, cancellationToken).ConfigureAwait(false);
            return new PackageSettingValueResponse(stored is not null, StoredValue: null, EffectiveValue: null);
        }

        var settings = GetSettings(loadedPackage);
        var storedValue = await settings.GetStoredValueAsync(key, cancellationToken).ConfigureAwait(false);
        var effectiveValue = storedValue ?? await settings.GetValueAsync(key, cancellationToken).ConfigureAwait(false);
        return new PackageSettingValueResponse(storedValue is not null, storedValue, effectiveValue);
    }

    public async Task SetValueAsync(
        ActiveLoadedPackage loadedPackage,
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        var field = GetCanonicalField(loadedPackage, key);
        ValidateValue(field, value);
        if (field.Kind == CanonicalSettingsFieldKind.Secret)
        {
            await loadedPackage.SecretsStore.SetSecretAsync(key, value, cancellationToken).ConfigureAwait(false);
            return;
        }

        await GetSettings(loadedPackage).SetValueAsync(key, value, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteValueAsync(
        ActiveLoadedPackage loadedPackage,
        string key,
        CancellationToken cancellationToken = default)
    {
        var field = GetCanonicalField(loadedPackage, key);
        if (field.Kind == CanonicalSettingsFieldKind.Secret)
        {
            if (field.IsRequired)
            {
                throw new RuntimeValidationException($"Setting '{field.Key}' requires a value.");
            }
            await loadedPackage.SecretsStore.DeleteSecretAsync(key, cancellationToken).ConfigureAwait(false);
            return;
        }

        await GetSettings(loadedPackage).DeleteValueAsync(key, cancellationToken).ConfigureAwait(false);
    }

    private static PackageSettingsFieldDescriptor[] GetProtocolFields(ActiveLoadedPackage loadedPackage)
        => loadedPackage.SettingsSchema?.Sections.SelectMany(section => section.Fields).ToArray()
            ?? throw new RuntimeNotFoundException(
                $"Package '{loadedPackage.Descriptor.PackageId}' does not declare a settings schema.");

    private static CanonicalSettingsField[] GetCanonicalFields(ActiveLoadedPackage loadedPackage)
        => loadedPackage.CanonicalSettingsSchema?.Sections.SelectMany(section => section.Fields).ToArray()
            ?? throw new RuntimeNotFoundException(
                $"Package '{loadedPackage.Descriptor.PackageId}' does not declare a settings schema.");

    private static CanonicalSettingsField GetCanonicalField(ActiveLoadedPackage loadedPackage, string key)
        => GetCanonicalFields(loadedPackage).FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.Ordinal))
            ?? throw new RuntimeNotFoundException(
                $"Setting '{key}' is not declared by package '{loadedPackage.Descriptor.PackageId}'.");

    private static IPackageSettings GetSettings(ActiveLoadedPackage loadedPackage)
        => loadedPackage.Settings;

    private static void ValidateValue(CanonicalSettingsField field, string? value)
    {
        if (field.Kind == CanonicalSettingsFieldKind.Secret)
        {
            if (field.IsRequired && string.IsNullOrWhiteSpace(value))
            {
                throw new RuntimeValidationException($"Setting '{field.Key}' requires a value.");
            }
            return;
        }

        try
        {
            StoredPackageSettings.ValidateValue(field, value);
        }
        catch (ArgumentException exception)
        {
            throw new RuntimeValidationException(exception.Message);
        }
    }
}
