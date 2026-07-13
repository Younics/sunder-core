using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSettingsService
{
    public IReadOnlyList<PackageConfigurationSchemaDescriptor> GetSchemas(
        IReadOnlyList<ActiveLoadedPackage> loadedPackages)
        => loadedPackages
            .Where(package => package.ConfigurationSchema is not null)
            .Select(package => package.ConfigurationSchema!)
            .OrderBy(schema => schema.PackageDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<PackageSettingsValuesResponse> GetValuesAsync(
        ActiveLoadedPackage loadedPackage,
        CancellationToken cancellationToken = default)
    {
        var fields = GetFields(loadedPackage);
        var storedValues = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in fields.Where(field => field.Kind != PackageConfigurationFieldKind.Secret))
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
                .Where(field => field.Kind == PackageConfigurationFieldKind.Secret && storedSecretKeys.Contains(field.Key))
                .Select(field => field.Key)
                .ToArray());
    }

    public async Task SaveValuesAsync(
        ActiveLoadedPackage loadedPackage,
        UpdatePackageSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var fields = GetFields(loadedPackage);
        var fieldsByKey = fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        foreach (var pair in request.Values)
        {
            if (!fieldsByKey.TryGetValue(pair.Key, out var field))
            {
                throw new RuntimeValidationException(
                    $"Setting '{pair.Key}' is not declared by package '{loadedPackage.Descriptor.PackageId}'.");
            }

            ValidateValue(field, pair.Value);
        }

        foreach (var field in fields.Where(field => field.Kind != PackageConfigurationFieldKind.Secret))
        {
            ValidateValue(field, request.Values.TryGetValue(field.Key, out var value) ? value : null);
        }

        foreach (var field in fields.Where(field => field.Kind != PackageConfigurationFieldKind.Secret))
        {
            if (!request.Values.TryGetValue(field.Key, out var value) || value is null)
            {
                await GetSettings(loadedPackage).DeleteValueAsync(field.Key, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await GetSettings(loadedPackage).SetValueAsync(field.Key, value, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var pair in request.Values)
        {
            var field = fieldsByKey[pair.Key];
            if (field.Kind != PackageConfigurationFieldKind.Secret)
            {
                continue;
            }

            if (pair.Value is null)
            {
                await loadedPackage.SecretsStore.DeleteSecretAsync(pair.Key, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await loadedPackage.SecretsStore.SetSecretAsync(pair.Key, pair.Value, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static PackageConfigurationFieldDescriptor[] GetFields(ActiveLoadedPackage loadedPackage)
        => loadedPackage.ConfigurationSchema?.Sections.SelectMany(section => section.Fields).ToArray()
            ?? throw new RuntimeNotFoundException(
                $"Package '{loadedPackage.Descriptor.PackageId}' does not declare a configuration schema.");

    private static IPackageSettings GetSettings(ActiveLoadedPackage loadedPackage)
        => loadedPackage.Settings;

    private static void ValidateValue(PackageConfigurationFieldDescriptor field, string? value)
    {
        var effectiveValue = value ?? field.DefaultValue;
        if (field.IsRequired && string.IsNullOrWhiteSpace(effectiveValue))
        {
            throw new RuntimeValidationException($"Setting '{field.Key}' requires a value.");
        }

        if (effectiveValue is null)
        {
            return;
        }

        if (field.Kind == PackageConfigurationFieldKind.Boolean && !bool.TryParse(effectiveValue, out _))
        {
            throw new RuntimeValidationException($"Setting '{field.Key}' must be 'true' or 'false'.");
        }

        if (field.Kind == PackageConfigurationFieldKind.Select
            && !field.Options.Any(option => string.Equals(option.Value, effectiveValue, StringComparison.Ordinal)))
        {
            throw new RuntimeValidationException($"Setting '{field.Key}' is not one of its declared options.");
        }
    }
}
