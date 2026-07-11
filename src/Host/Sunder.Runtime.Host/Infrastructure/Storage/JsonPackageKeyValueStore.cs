using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed class JsonPackageKeyValueStore : IPackageKeyValueStore
{
    private const string StateFormat = "sunder.package-state";
    private const int StateVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly AtomicFileDocument _document;

    internal JsonPackageKeyValueStore(string filePath)
        : this(filePath, null)
    {
    }

    internal JsonPackageKeyValueStore(string filePath, AtomicFileSystem? fileSystem)
    {
        _document = new AtomicFileDocument(
            filePath,
            sensitive: false,
            StorageCommitPhase.StateDocument,
            fileSystem);
    }

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _document.ExecuteAsync(transaction =>
        {
            var state = Load(transaction);
            return state.Values.TryGetValue(key, out var value) ? value : null;
        }, cancellationToken);
    }

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return _document.ExecuteAsync(transaction =>
        {
            var state = Load(transaction);
            var nextRevision = checked(state.Revision + 1);
            state.Values[key] = value;
            Save(transaction, state.Values, nextRevision);
            return true;
        }, cancellationToken);
    }

    public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _document.ExecuteAsync(
            transaction => Load(transaction).Values.ContainsKey(key),
            cancellationToken);
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _document.ExecuteAsync(transaction =>
        {
            var state = Load(transaction);
            if (state.Values.ContainsKey(key))
            {
                var nextRevision = checked(state.Revision + 1);
                state.Values.Remove(key);
                Save(transaction, state.Values, nextRevision);
            }

            return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(
        string? prefix = null,
        CancellationToken cancellationToken = default) =>
        _document.ExecuteAsync<IReadOnlyList<string>>(transaction =>
        {
            return Load(transaction).Values.Keys
                .Where(key => string.IsNullOrWhiteSpace(prefix)
                    || key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
        }, cancellationToken);

    public void ResetAfterFailure() => _document.Execute(transaction => transaction.ResetFailureMarker());

    private static StateDocument Load(AtomicFileTransaction transaction)
    {
        var contents = transaction.Read();
        if (contents is null)
        {
            return new StateDocument([], 0);
        }

        StateDocument state;
        try
        {
            state = Parse(contents, transaction.FilePath);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw FailClosed(transaction, contents, "structural-corruption", exception);
        }

        return state;
    }

    private static StateDocument Parse(byte[] contents, string canonicalPath)
    {
        using var json = JsonDocument.Parse(contents);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The state document root must be an object.");
        }

        var root = json.RootElement;
        if (TryReadStringDictionary(root, out _))
        {
            throw new PackageStorageNotSupportedException(
                "Legacy package state is not supported in the Runtime V1 state root.");
        }

        if (StorageFailureMarker.IsMarker(root))
        {
            throw StorageFailureMarker.CreateException(root, canonicalPath);
        }

        var format = ReadRequiredString(root, "format");
        if (!string.Equals(format, StateFormat, StringComparison.Ordinal))
        {
            throw new PackageStorageNotSupportedException($"Unsupported package state format '{format}'.");
        }

        var version = ReadRequiredInt32(root, "version");
        if (version != StateVersion)
        {
            throw new PackageStorageNotSupportedException(
                $"Package state version {version} is not supported; this runtime supports version {StateVersion}.");
        }

        var revision = ReadRequiredInt64(root, "revision");
        if (revision < 0)
        {
            throw new InvalidDataException("The state revision is invalid.");
        }

        if (!root.TryGetProperty("values", out var valuesElement))
        {
            throw new InvalidDataException("The state document does not contain values.");
        }

        return new StateDocument(ReadStringDictionary(valuesElement), revision);
    }

    private static void Save(AtomicFileTransaction transaction, Dictionary<string, string> values, long revision)
    {
        var envelope = new StateEnvelope(StateFormat, StateVersion, revision, values);
        transaction.Write(JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions));
    }

    private static bool TryReadStringDictionary(JsonElement element, out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                values = null!;
                return false;
            }

            values[property.Name] = property.Value.GetString()!;
        }

        return true;
    }

    private static Dictionary<string, string> ReadStringDictionary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Stored values must be a JSON object.");
        }

        if (!TryReadStringDictionary(element, out var values))
        {
            throw new InvalidDataException("Every stored state value must be a string.");
        }

        return values;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"The state document has an invalid '{propertyName}'.");
        }

        return property.GetString()!;
    }

    private static int ReadRequiredInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"The state document has an invalid '{propertyName}'.");
        }

        return value;
    }

    private static long ReadRequiredInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || !property.TryGetInt64(out var value))
        {
            throw new InvalidDataException($"The state document has an invalid '{propertyName}'.");
        }

        return value;
    }

    private static PackageStorageException FailClosed(
        AtomicFileTransaction transaction,
        byte[] contents,
        string failureKind,
        Exception cause)
    {
        try
        {
            var quarantinePath = transaction.QuarantineAndReplaceWithFailure(
                contents,
                "opaque-copy",
                failureKind);
            return new PackageStorageRecoveryRequiredException(
                $"Corrupt package state was quarantined at '{quarantinePath}'. Explicit recovery or reset is required.",
                quarantinePath,
                cause);
        }
        catch (Exception quarantineException)
        {
            return new PackageStorageCorruptionException(
                "Package state is corrupt, and a durable recovery marker could not be committed. "
                    + "The original canonical document remains authoritative and was not overwritten.",
                new AggregateException(cause, quarantineException));
        }
    }

    private sealed record StateEnvelope(
        string Format,
        int Version,
        long Revision,
        Dictionary<string, string> Values);

    private sealed record StateDocument(
        Dictionary<string, string> Values,
        long Revision);
}
