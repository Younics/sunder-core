using System.Text.Json;

namespace Sunder.Cli;

internal sealed partial class RuntimeStackCommandHandler
{
    private async Task<Dictionary<string, string>> ReadInputsAsync(ImportStackCommand command, CancellationToken token)
    {
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (command.InputsFromStdin)
        {
            using var value = await inputReader.ReadAsync(PackageSecretSource.StandardInput, null, token).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(value.Value);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                foreach (var property in document.RootElement.EnumerateObject())
                    if (inputs.Count >= 128 || string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 4096
                        || property.Value.ValueKind != JsonValueKind.String || !inputs.TryAdd(property.Name, property.Value.GetString()!))
                        throw new JsonException();
            }
            catch (JsonException)
            {
                // Never include parser details: they can contain submitted input values.
                throw new CliUsageException("Stack inputs must be a JSON object containing at most 128 unique scoped input ids with string values.");
            }
        }
        foreach (var source in command.InputEnvironmentSources ?? [])
        {
            var separator = source.IndexOf('=');
            if (separator <= 0 || separator == source.Length - 1)
                throw new CliUsageException("Use '--input-env scoped-input-id=ENVIRONMENT_VARIABLE'.");
            var id = source[..separator];
            using var value = await inputReader.ReadAsync(PackageSecretSource.Environment, source[(separator + 1)..], token).ConfigureAwait(false);
            if (!inputs.TryAdd(id, value.Value)) throw new CliUsageException("A Stack input was supplied more than once.");
        }
        return inputs;
    }
}
