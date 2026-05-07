using Sunder.Protocol;

namespace Sunder.Runtime.Host.Services;

internal sealed class DevPackageConfigurationService
{
    public IReadOnlyList<PackageConfigurationSchemaDescriptor> GetConfigurationSchemas(
        IReadOnlyList<ActiveLoadedDevPackage> loadedPackages)
    {
        return loadedPackages
            .Where(package => package.ConfigurationSchema is not null)
            .Select(package => package.ConfigurationSchema!)
            .OrderBy(schema => schema.PackageDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<PackageConfigurationValuesResponse> GetConfigurationValuesAsync(
        ActiveLoadedDevPackage loadedPackage,
        CancellationToken cancellationToken = default)
    {
        var keys = await loadedPackage.StateStore.ListKeysAsync(cancellationToken: cancellationToken);
        var secretKeys = loadedPackage.ConfigurationSchema?.Sections
            .SelectMany(section => section.Fields)
            .Where(field => field.Kind == PackageConfigurationFieldKind.Secret)
            .Select(field => field.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (secretKeys.Contains(key))
            {
                continue;
            }

            values[key] = await loadedPackage.StateStore.GetValueAsync(key, cancellationToken);
        }

        return new PackageConfigurationValuesResponse(loadedPackage.Descriptor.PackageId, values, loadedPackage.SecretKeys);
    }

    public async Task<bool> SaveConfigurationValuesAsync(
        ActiveLoadedDevPackage loadedPackage,
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
                await loadedPackage.StateStore.DeleteValueAsync(pair.Key, cancellationToken);

                if (pair.Value is null)
                {
                    loadedPackage.SecretsStore.DeleteSecret(pair.Key);
                }
                else if (!string.IsNullOrWhiteSpace(pair.Value))
                {
                    loadedPackage.SecretsStore.SetSecret(pair.Key, pair.Value);
                }

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
}
