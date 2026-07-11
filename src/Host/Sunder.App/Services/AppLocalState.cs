using System.Text.Json;

namespace Sunder.App.Services;

internal static class AppLocalState
{
    internal const int SchemaVersion = 1;
    internal const string Product = "Sunder";
    internal const string Api = "sunder.app.local-state";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal static string RootPath
    {
        get
        {
            var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                throw new InvalidOperationException("The current user's local application data directory is unavailable.");
            }

            return Path.Combine(localApplicationData, "Sunder", "app", "v1");
        }
    }

    internal static string GetPath(params string[] segments)
        => Path.Combine([RootPath, .. segments]);

    internal static void EnsureInitializedForStartup()
        => TemporaryV1TesterBootstrapCleanup.EnsureInitialized(RootPath, EnsureInitialized);

    internal static void EnsureInitialized(string? rootPath = null)
    {
        var root = Path.GetFullPath(rootPath ?? RootPath);
        Directory.CreateDirectory(root);
        var schemaPath = Path.Combine(root, "schema.json");
        if (File.Exists(schemaPath))
        {
            Validate(schemaPath);
            return;
        }

        if (Directory.EnumerateFileSystemEntries(root)
            .Any(path => !Path.GetFileName(path).StartsWith(".schema.json.", StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The App V1 state root is non-empty but has no compatible schema metadata. Legacy state is not inferred or migrated.");
        }

        var temporaryPath = Path.Combine(root, $".schema.json.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new StateSchema(Product, Api, SchemaVersion), JsonOptions);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, schemaPath);
        }
        catch (IOException) when (File.Exists(schemaPath))
        {
            Validate(schemaPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void Validate(string schemaPath)
    {
        StateSchema? schema;
        try
        {
            schema = JsonSerializer.Deserialize<StateSchema>(File.ReadAllText(schemaPath), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The App V1 state schema metadata is invalid.", exception);
        }

        if (schema is null
            || schema.SchemaVersion != SchemaVersion
            || !string.Equals(schema.Product, Product, StringComparison.Ordinal)
            || !string.Equals(schema.Api, Api, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The App V1 state schema metadata is incompatible with this App.");
        }
    }

    private sealed record StateSchema(string Product, string Api, int SchemaVersion);
}
