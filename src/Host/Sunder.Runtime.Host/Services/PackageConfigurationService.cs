using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageConfigurationService
{
    public IReadOnlyList<PackageConfigurationSchemaDescriptor> GetConfigurationSchemas(
        IReadOnlyList<ActiveLoadedPackage> loadedPackages)
    {
        return loadedPackages
            .Where(package => package.ConfigurationSchema is not null)
            .Select(package => package.ConfigurationSchema!)
            .OrderBy(schema => schema.PackageDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<PackageConfigurationValuesResponse> GetConfigurationValuesAsync(
        ActiveLoadedPackage loadedPackage,
        CancellationToken cancellationToken = default)
    {
        var secretFieldsByKey = loadedPackage.ConfigurationSchema?.Sections
            .SelectMany(section => section.Fields)
            .Where(field => field.Kind == PackageConfigurationFieldKind.Secret)
            .ToDictionary(field => field.Key, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, PackageConfigurationFieldDescriptor>(StringComparer.OrdinalIgnoreCase);

        await MigrateLegacySecretsAsync(loadedPackage, secretFieldsByKey, cancellationToken);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in await loadedPackage.StateStore.ListKeysAsync(cancellationToken: cancellationToken))
        {
            if (secretFieldsByKey.ContainsKey(key))
            {
                continue;
            }

            values[key] = await loadedPackage.StateStore.GetValueAsync(key, cancellationToken);
        }

        return new PackageConfigurationValuesResponse(
            loadedPackage.Descriptor.PackageId,
            values,
            await loadedPackage.SecretsStore.ListKeysAsync(cancellationToken));
    }

    public async Task<bool> SaveConfigurationValuesAsync(
        ActiveLoadedPackage loadedPackage,
        UpdatePackageConfigurationValuesRequest request,
        CancellationToken cancellationToken = default)
    {
        if (loadedPackage.ConfigurationSchema is null)
        {
            return false;
        }

        var fieldsByKey = loadedPackage.ConfigurationSchema.Sections
            .SelectMany(section => section.Fields)
            .ToDictionary(field => field.Key, StringComparer.OrdinalIgnoreCase);

        await MigrateLegacySecretsAsync(
            loadedPackage,
            fieldsByKey
                .Where(pair => pair.Value.Kind == PackageConfigurationFieldKind.Secret)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            cancellationToken);

        var allowedKeys = fieldsByKey.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var existingKey in await loadedPackage.StateStore.ListKeysAsync(cancellationToken: cancellationToken))
        {
            if (allowedKeys.Contains(existingKey)
                && fieldsByKey.TryGetValue(existingKey, out var field)
                && field.Kind != PackageConfigurationFieldKind.Secret
                && !request.Values.ContainsKey(existingKey))
            {
                await loadedPackage.StateStore.DeleteValueAsync(existingKey, cancellationToken);
            }
        }

        foreach (var pair in request.Values)
        {
            if (!fieldsByKey.TryGetValue(pair.Key, out var field))
            {
                continue;
            }

            if (field.Kind == PackageConfigurationFieldKind.Secret)
            {
                if (pair.Value is null)
                {
                    await loadedPackage.SecretsStore.DeleteSecretAsync(pair.Key, cancellationToken);
                }
                else if (!string.IsNullOrWhiteSpace(pair.Value))
                {
                    await loadedPackage.SecretsStore.SetSecretAsync(pair.Key, pair.Value, cancellationToken);
                }

                await loadedPackage.StateStore.DeleteValueAsync(pair.Key, cancellationToken);

                continue;
            }

            if (pair.Value is null)
            {
                await loadedPackage.StateStore.DeleteValueAsync(pair.Key, cancellationToken);
            }
            else
            {
                await loadedPackage.StateStore.SetValueAsync(pair.Key, pair.Value, cancellationToken);
            }
        }

        return true;
    }

    private static async Task MigrateLegacySecretsAsync(
        ActiveLoadedPackage loadedPackage,
        IReadOnlyDictionary<string, PackageConfigurationFieldDescriptor> secretFieldsByKey,
        CancellationToken cancellationToken)
    {
        if (secretFieldsByKey.Count == 0)
        {
            return;
        }

        var storedSecretKeys = (await loadedPackage.SecretsStore.ListKeysAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stateKey in await loadedPackage.StateStore.ListKeysAsync(cancellationToken: cancellationToken))
        {
            if (!secretFieldsByKey.TryGetValue(stateKey, out var field))
            {
                continue;
            }

            var legacyValue = await loadedPackage.StateStore.GetValueAsync(stateKey, cancellationToken);
            if (legacyValue is null)
            {
                continue;
            }

            if (!storedSecretKeys.Contains(field.Key))
            {
                await loadedPackage.SecretsStore.SetSecretAsync(field.Key, legacyValue, cancellationToken);
                storedSecretKeys.Add(field.Key);
            }

            await loadedPackage.StateStore.DeleteValueAsync(stateKey, cancellationToken);
        }
    }
}
