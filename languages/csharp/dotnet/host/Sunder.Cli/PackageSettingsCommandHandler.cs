using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed class PackageSettingsCommandHandler(
    ICliRuntimePackageSettingsClient runtime,
    CliOutput output,
    IPackageSecretValueReader secretValues)
{
    public async Task<int> ExecuteAsync(ShowPackageConfigSchemaCommand command, CancellationToken token)
    {
        var schema = await FindSchemaAsync(command.PackageId, token).ConfigureAwait(false);
        if (schema is null) return NotFound(command.PackageId);
        output.Data(ProjectSchema(schema));
        output.Line($"Configuration schema: {schema.PackageDisplayName} ({schema.PackageId})");
        foreach (var section in schema.Sections)
        {
            output.Line($"{section.Title}:");
            foreach (var field in section.Fields)
            {
                var required = field.IsRequired ? ", required" : string.Empty;
                output.Line($"  {field.Key} ({Kind(field.Kind)}{required}) - {field.Label}");
            }
        }
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(ListPackageConfigCommand command, CancellationToken token)
    {
        var schema = await FindSchemaAsync(command.PackageId, token).ConfigureAwait(false);
        if (schema is null) return NotFound(command.PackageId);
        var values = await runtime.GetPackageSettingsValuesAsync(command.PackageId, token).ConfigureAwait(false);
        if (values is null) return NotFound(command.PackageId);
        var fields = Fields(schema)
            .Where(field => field.Kind != PackageSettingsFieldKind.Secret)
            .Select(field =>
            {
                var stored = values.StoredValues.TryGetValue(field.Key, out var storedValue);
                return new ConfigValue(field, stored, storedValue, storedValue ?? field.DefaultValue);
            })
            .ToArray();
        output.Data(new
        {
            packageId = schema.PackageId,
            fields = fields.Select(ProjectConfigValue).ToArray(),
        });
        if (fields.Length == 0)
        {
            output.Info($"Package '{schema.PackageId}' declares no non-secret configuration.");
            return CliExitCodes.Success;
        }
        foreach (var value in fields)
            output.Line($"{value.Field.Key}: {value.EffectiveValue ?? "-"}{(value.IsStored ? " (stored)" : " (default/unset)")}");
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(GetPackageConfigCommand command, CancellationToken token)
    {
        var field = await FindFieldAsync(command.PackageId, command.Key, token).ConfigureAwait(false);
        if (field is null) return NotFound(command.PackageId, command.Key);
        if (field.Kind == PackageSettingsFieldKind.Secret) return WrongSurface(command.Key, secret: true);
        var value = await runtime.GetPackageSettingValueAsync(command.PackageId, command.Key, token).ConfigureAwait(false);
        output.Data(new
        {
            packageId = command.PackageId,
            key = command.Key,
            stored = value.IsStored,
            storedValue = value.StoredValue,
            effectiveValue = value.EffectiveValue,
        });
        output.Line(value.EffectiveValue ?? string.Empty);
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(SetPackageConfigCommand command, CancellationToken token)
    {
        var field = await FindFieldAsync(command.PackageId, command.Key, token).ConfigureAwait(false);
        if (field is null) return NotFound(command.PackageId, command.Key);
        if (field.Kind == PackageSettingsFieldKind.Secret) return WrongSurface(command.Key, secret: true);
        var value = ValidateAndNormalize(field, command.Value);
        await runtime.SetPackageSettingValueAsync(command.PackageId, command.Key, value, token).ConfigureAwait(false);
        output.Data(new { packageId = command.PackageId, key = command.Key, stored = true });
        output.Success($"Set configuration '{command.Key}' for package '{command.PackageId}'.");
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(UnsetPackageConfigCommand command, CancellationToken token)
    {
        var field = await FindFieldAsync(command.PackageId, command.Key, token).ConfigureAwait(false);
        if (field is null) return NotFound(command.PackageId, command.Key);
        if (field.Kind == PackageSettingsFieldKind.Secret) return WrongSurface(command.Key, secret: true);
        if (field.IsRequired && field.DefaultValue is null)
        {
            output.Error($"Configuration '{field.Key}' is required and has no default value.", "cli.package.config.required");
            return CliExitCodes.Usage;
        }
        await runtime.DeletePackageSettingValueAsync(command.PackageId, command.Key, token).ConfigureAwait(false);
        output.Data(new { packageId = command.PackageId, key = command.Key, stored = false });
        output.Success($"Unset configuration '{command.Key}' for package '{command.PackageId}'.");
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(PackageSecretStatusCommand command, CancellationToken token)
    {
        var schema = await FindSchemaAsync(command.PackageId, token).ConfigureAwait(false);
        if (schema is null) return NotFound(command.PackageId);
        var values = await runtime.GetPackageSettingsValuesAsync(command.PackageId, token).ConfigureAwait(false);
        if (values is null) return NotFound(command.PackageId);
        var stored = values.StoredSecretKeys.ToHashSet(StringComparer.Ordinal);
        var fields = Fields(schema)
            .Where(field => field.Kind == PackageSettingsFieldKind.Secret)
            .Select(field => new { key = field.Key, label = field.Label, required = field.IsRequired, configured = stored.Contains(field.Key) })
            .ToArray();
        output.Data(new { packageId = schema.PackageId, fields });
        if (fields.Length == 0)
        {
            output.Info($"Package '{schema.PackageId}' declares no secrets.");
            return CliExitCodes.Success;
        }
        foreach (var field in fields)
            output.Line($"{field.key}: {(field.configured ? "configured" : "not configured")}{(field.required ? " (required)" : string.Empty)}");
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(SetPackageSecretCommand command, CancellationToken token)
    {
        var field = await FindFieldAsync(command.PackageId, command.Key, token).ConfigureAwait(false);
        if (field is null) return NotFound(command.PackageId, command.Key);
        if (field.Kind != PackageSettingsFieldKind.Secret) return WrongSurface(command.Key, secret: false);
        using var value = await secretValues.ReadAsync(command.Source, command.EnvironmentVariable, token).ConfigureAwait(false);
        await runtime.SetPackageSettingValueAsync(command.PackageId, command.Key, value.Value, token).ConfigureAwait(false);
        output.Data(new { packageId = command.PackageId, key = command.Key, configured = true });
        output.Success($"Set secret '{command.Key}' for package '{command.PackageId}'.");
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(UnsetPackageSecretCommand command, CancellationToken token)
    {
        var field = await FindFieldAsync(command.PackageId, command.Key, token).ConfigureAwait(false);
        if (field is null) return NotFound(command.PackageId, command.Key);
        if (field.Kind != PackageSettingsFieldKind.Secret) return WrongSurface(command.Key, secret: false);
        await runtime.DeletePackageSettingValueAsync(command.PackageId, command.Key, token).ConfigureAwait(false);
        output.Data(new { packageId = command.PackageId, key = command.Key, configured = false });
        output.Success($"Unset secret '{command.Key}' for package '{command.PackageId}'.");
        return CliExitCodes.Success;
    }

    private async Task<PackageSettingsSchemaDescriptor?> FindSchemaAsync(string packageId, CancellationToken token)
        => (await runtime.GetPackageSettingsSchemasAsync(token).ConfigureAwait(false))
            .FirstOrDefault(schema => string.Equals(schema.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    private async Task<PackageSettingsFieldDescriptor?> FindFieldAsync(string packageId, string key, CancellationToken token)
        => (await FindSchemaAsync(packageId, token).ConfigureAwait(false)) is { } schema
            ? Fields(schema).FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.Ordinal))
            : null;

    private int NotFound(string packageId, string? key = null)
    {
        output.Error(
            key is null
                ? $"Active package '{packageId}' does not declare a configuration schema."
                : $"Setting '{key}' is not declared by active package '{packageId}'.",
            "cli.resource.not_found");
        return CliExitCodes.NotFound;
    }

    private int WrongSurface(string key, bool secret)
    {
        output.Error(
            secret
                ? $"Setting '{key}' is secret. Use 'package secret' commands."
                : $"Setting '{key}' is not secret. Use 'package config' commands.",
            "cli.usage");
        return CliExitCodes.Usage;
    }

    private static string ValidateAndNormalize(PackageSettingsFieldDescriptor field, string value)
    {
        if (field.IsRequired && string.IsNullOrWhiteSpace(value))
            throw new CliUsageException($"Configuration '{field.Key}' requires a value.");
        if (field.Kind == PackageSettingsFieldKind.Boolean)
        {
            if (!bool.TryParse(value, out var parsed))
                throw new CliUsageException($"Configuration '{field.Key}' must be 'true' or 'false'.");
            return parsed ? "true" : "false";
        }
        if (field.Kind == PackageSettingsFieldKind.Select
            && !field.Options.Any(option => string.Equals(option.Value, value, StringComparison.Ordinal)))
        {
            throw new CliUsageException(
                $"Configuration '{field.Key}' must be one of: {string.Join(", ", field.Options.Select(option => option.Value))}.");
        }
        return value;
    }

    private static IEnumerable<PackageSettingsFieldDescriptor> Fields(PackageSettingsSchemaDescriptor schema)
        => schema.Sections.SelectMany(section => section.Fields);

    private static string Kind(PackageSettingsFieldKind kind) => kind.ToString().ToLowerInvariant();

    private static object ProjectSchema(PackageSettingsSchemaDescriptor schema) => new
    {
        packageId = schema.PackageId,
        name = schema.PackageDisplayName,
        summary = schema.Summary,
        sections = schema.Sections.Select(section => new
        {
            sectionId = section.SectionId,
            title = section.Title,
            description = section.Description,
            fields = section.Fields.Select(field => new
            {
                key = field.Key,
                label = field.Label,
                kind = Kind(field.Kind),
                description = field.Description,
                required = field.IsRequired,
                placeholder = field.Placeholder,
                defaultValue = field.Kind == PackageSettingsFieldKind.Secret ? null : field.DefaultValue,
                options = field.Options.Select(option => new { value = option.Value, label = option.Label }).ToArray(),
            }).ToArray(),
        }).ToArray(),
    };

    private static object ProjectConfigValue(ConfigValue value) => new
    {
        key = value.Field.Key,
        label = value.Field.Label,
        kind = Kind(value.Field.Kind),
        required = value.Field.IsRequired,
        stored = value.IsStored,
        storedValue = value.StoredValue,
        effectiveValue = value.EffectiveValue,
    };

    private sealed record ConfigValue(
        PackageSettingsFieldDescriptor Field,
        bool IsStored,
        string? StoredValue,
        string? EffectiveValue);
}
