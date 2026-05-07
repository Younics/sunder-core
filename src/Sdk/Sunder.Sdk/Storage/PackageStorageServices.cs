using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Sdk.Storage;

public sealed class LocalPackageStorageContext : IPackageStorageContext
{
    public LocalPackageStorageContext(string packageId)
    {
        var packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sunder",
            "Packages",
            SanitizePathSegment(packageId)
        );

        DataRootPath = Path.Combine(packageRoot, "data");
        CacheRootPath = Path.Combine(packageRoot, "cache");
        LogsRootPath = Path.Combine(packageRoot, "logs");

        Directory.CreateDirectory(DataRootPath);
        Directory.CreateDirectory(CacheRootPath);
        Directory.CreateDirectory(LogsRootPath);

        var filesRootPath = Path.Combine(packageRoot, "files");
        Directory.CreateDirectory(filesRootPath);

        Files = new LocalPackageFileStore(filesRootPath);
        State = new JsonPackageKeyValueStore(Path.Combine(DataRootPath, "state.json"));
    }

    public string DataRootPath { get; }

    public string CacheRootPath { get; }

    public string LogsRootPath { get; }

    public IPackageFileStore Files { get; }

    public IPackageKeyValueStore State { get; }

    private static string SanitizePathSegment(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(value
            .Select(ch => invalidCharacters.Contains(ch) || ch is ':' or '/' or '\\' ? '_' : ch)
            .ToArray());
    }
}

public sealed class PackageStateConfiguration(IPackageKeyValueStore stateStore) : IPackageConfiguration
{
    public string? GetValue(string key) => stateStore.GetValue(key);
}

public sealed class JsonPackageSecretsStore(string filePath) : IPackageSecrets
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _syncRoot = new();
    private readonly string _filePath = filePath;

    public string? GetSecret(string key)
    {
        lock (_syncRoot)
        {
            return LoadSecrets().TryGetValue(key, out var value) ? value : null;
        }
    }

    public IReadOnlyList<string> ListKeys()
    {
        lock (_syncRoot)
        {
            return LoadSecrets().Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public void SetSecret(string key, string value)
    {
        lock (_syncRoot)
        {
            var secrets = LoadSecrets();
            secrets[key] = value;
            SaveSecrets(secrets);
        }
    }

    public void DeleteSecret(string key)
    {
        lock (_syncRoot)
        {
            var secrets = LoadSecrets();
            if (secrets.Remove(key))
            {
                SaveSecrets(secrets);
            }
        }
    }

    private Dictionary<string, string> LoadSecrets()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath), JsonOptions) ?? [];
        }
        catch
        {
            QuarantineCorruptFile(_filePath);
            return [];
        }
    }

    private void SaveSecrets(Dictionary<string, string> secrets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(secrets, JsonOptions));
    }

    private static void QuarantineCorruptFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Move(filePath, $"{filePath}.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}");
            }
        }
        catch
        {
            // A corrupt secrets file should not block package startup.
        }
    }
}

public sealed class LocalPackageFileStore(string rootPath) : IPackageFileStore
{
    public string RootPath { get; } = rootPath;

    public string GetPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return RootPath;
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Package file paths must be relative to the package file store root.");
        }

        var normalizedSegments = relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment is not ".")
            .ToArray();
        if (normalizedSegments.Any(segment => segment == ".."))
        {
            throw new InvalidOperationException("Package file paths must not contain parent directory traversal segments.");
        }

        return normalizedSegments.Length == 0
            ? RootPath
            : Path.Combine([RootPath, .. normalizedSegments]);
    }
}

public sealed class JsonPackageKeyValueStore(string filePath) : IPackageKeyValueStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _syncRoot = new();
    private readonly string _filePath = filePath;

    public string? GetValue(string key)
    {
        lock (_syncRoot)
        {
            return LoadState().TryGetValue(key, out var value) ? value : null;
        }
    }

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetValue(key));
    }

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            var state = LoadState();
            state[key] = value;
            SaveState(state);
            return Task.CompletedTask;
        }
    }

    public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            return Task.FromResult(LoadState().ContainsKey(key));
        }
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            var state = LoadState();
            if (state.Remove(key))
            {
                SaveState(state);
            }

            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            var keys = LoadState().Keys
                .Where(key => string.IsNullOrWhiteSpace(prefix) || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Task.FromResult<IReadOnlyList<string>>(keys);
        }
    }

    private Dictionary<string, string> LoadState()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath), JsonOptions) ?? [];
        }
        catch
        {
            QuarantineCorruptFile(_filePath);
            return [];
        }
    }

    private void SaveState(Dictionary<string, string> state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    private static void QuarantineCorruptFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Move(filePath, $"{filePath}.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}");
            }
        }
        catch
        {
            // A corrupt state file should not block package startup.
        }
    }
}
