using System.Text.Json;

namespace Sunder.Runtime.LocalState;

public static class RuntimeLocalState
{
    public const string StateRootEnvironmentVariable = "SUNDER_RUNTIME_STATE_ROOT";
    public const int SchemaVersion = 1;
    public const string Product = "Sunder";
    public const string Api = "sunder.runtime.local-state";
    public const string SchemaFileName = "schema.json";
    public const string LeaseFileName = "runtime.lock";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string GetV1RootPath()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(StateRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            if (!Path.IsPathFullyQualified(configuredRoot))
            {
                throw new InvalidOperationException($"{StateRootEnvironmentVariable} must be an absolute path.");
            }

            return Path.GetFullPath(configuredRoot);
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The current user's local application data directory is unavailable.");
        }

        return Path.Combine(
            localApplicationData,
            RuntimeV1StateDescriptor.RootProductDirectory,
            RuntimeV1StateDescriptor.RootRuntimeDirectory,
            RuntimeV1StateDescriptor.RootVersionDirectory);
    }

    public static void EnsureInitialized(string? rootPath = null)
    {
        var root = Path.GetFullPath(rootPath ?? GetV1RootPath());
        Directory.CreateDirectory(root);
        var schemaPath = Path.Combine(root, SchemaFileName);
        if (File.Exists(schemaPath))
        {
            ValidateSchema(schemaPath);
            DeleteStaleSchemaTemps(root);
            return;
        }

        var unexpectedEntries = Directory.EnumerateFileSystemEntries(root)
            .Where(path => !string.Equals(Path.GetFileName(path), LeaseFileName, StringComparison.Ordinal)
                           && !IsSchemaTemp(path))
            .ToArray();
        if (unexpectedEntries.Length != 0)
        {
            throw new InvalidDataException(
                "The Runtime V1 state root is non-empty but has no compatible schema metadata. Legacy state is not inferred or migrated.");
        }

        var temporaryPath = Path.Combine(root, $".{SchemaFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new StateSchema(Product, Api, SchemaVersion),
                JsonOptions);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, schemaPath);
            DeleteStaleSchemaTemps(root, temporaryPath);
        }
        catch (IOException) when (File.Exists(schemaPath))
        {
            ValidateSchema(schemaPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static void Validate(string? rootPath = null)
    {
        var root = Path.GetFullPath(rootPath ?? GetV1RootPath());
        if (!Directory.Exists(root))
        {
            return;
        }

        var schemaPath = Path.Combine(root, SchemaFileName);
        if (!File.Exists(schemaPath))
        {
            if (Directory.EnumerateFileSystemEntries(root)
                .Any(path => !string.Equals(Path.GetFileName(path), LeaseFileName, StringComparison.Ordinal)
                             && !IsSchemaTemp(path)))
            {
                throw new InvalidDataException("The Runtime V1 state root has no schema metadata and cannot be reset safely.");
            }

            return;
        }

        ValidateSchema(schemaPath);
        DeleteStaleSchemaTemps(root);
    }

    private static bool IsSchemaTemp(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith($".{SchemaFileName}.", StringComparison.Ordinal)
               && fileName.EndsWith(".tmp", StringComparison.Ordinal);
    }

    private static void DeleteStaleSchemaTemps(string root, string? except = null)
    {
        foreach (var path in Directory.EnumerateFiles(root).Where(IsSchemaTemp))
        {
            if (except is not null && string.Equals(path, except, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void ValidateSchema(string schemaPath)
    {
        StateSchema? schema;
        try
        {
            schema = JsonSerializer.Deserialize<StateSchema>(File.ReadAllText(schemaPath), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Runtime V1 state schema metadata is invalid.", exception);
        }

        if (schema is null
            || schema.SchemaVersion != SchemaVersion
            || !string.Equals(schema.Product, Product, StringComparison.Ordinal)
            || !string.Equals(schema.Api, Api, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Runtime V1 state schema metadata is incompatible with this Runtime.");
        }
    }

    private sealed record StateSchema(string Product, string Api, int SchemaVersion);
}
