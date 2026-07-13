using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Configuration;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed class LocalPackageStorageContext : IPackageStorageContext
{
    internal LocalPackageStorageContext(string packageId, string packageDataRootPath)
    {
        var packageRoot = Path.Combine(
            Path.GetFullPath(packageDataRootPath),
            SanitizePathSegment(packageId)
        );

        DataRootPath = Path.Combine(packageRoot, "data");
        LogsRootPath = Path.Combine(packageRoot, "logs");
        var workspaceRootPath = Path.Combine(packageRoot, "workspace");

        Directory.CreateDirectory(DataRootPath);
        Directory.CreateDirectory(LogsRootPath);
        Directory.CreateDirectory(workspaceRootPath);

        var filesRootPath = Path.Combine(packageRoot, "files");
        Directory.CreateDirectory(filesRootPath);

        Files = new LocalPackageFileStore(filesRootPath);
        State = new JsonPackageKeyValueStore(Path.Combine(DataRootPath, "state.json"));
        SettingsStore = new JsonPackageKeyValueStore(Path.Combine(DataRootPath, "settings.json"));
        RoleLocalWorkspace = new LocalPackageRoleLocalWorkspace(workspaceRootPath);
    }

    internal string DataRootPath { get; }

    internal string LogsRootPath { get; }

    public IPackageFileStore Files { get; }

    public IPackageKeyValueStore State { get; }

    internal JsonPackageKeyValueStore SettingsStore { get; }

    public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; }

    private static string SanitizePathSegment(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(value
            .Select(ch => invalidCharacters.Contains(ch) || ch is ':' or '/' or '\\' ? '_' : ch)
            .ToArray());
    }
}

internal sealed class PackageSettings(JsonPackageKeyValueStore store) : IPackageSettings
{
    internal PackageConfigurationSchema? Schema { private get; set; }

    public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        var field = GetField(key);
        return await store.GetValueAsync(key, cancellationToken).ConfigureAwait(false) ?? field.DefaultValue;
    }

    public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
    {
        GetField(key);
        return store.GetValueAsync(key, cancellationToken);
    }

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var field = GetField(key);
        ValidateValue(field, value);
        return store.SetValueAsync(key, value, cancellationToken);
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        var field = GetField(key);
        if (field.IsRequired && string.IsNullOrWhiteSpace(field.DefaultValue))
        {
            throw new ArgumentException($"Setting '{field.Key}' requires a stored value.", nameof(key));
        }

        return store.DeleteValueAsync(key, cancellationToken);
    }

    private PackageConfigurationField GetField(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var field = Schema?.Sections
            .SelectMany(section => section.Fields)
            .FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));
        if (field is null)
        {
            throw new ArgumentException($"Setting '{key}' is not declared by the package configuration schema.", nameof(key));
        }

        if (field.Kind == PackageConfigurationFieldKind.Secret)
        {
            throw new ArgumentException($"Setting '{key}' is secret and must be accessed through package secrets.", nameof(key));
        }

        return field;
    }

    private static void ValidateValue(PackageConfigurationField field, string value)
    {
        if (field.IsRequired && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Setting '{field.Key}' requires a value.", nameof(value));
        }

        if (field.Kind == PackageConfigurationFieldKind.Boolean && !bool.TryParse(value, out _))
        {
            throw new ArgumentException($"Setting '{field.Key}' must be 'true' or 'false'.", nameof(value));
        }

        if (field.Kind == PackageConfigurationFieldKind.Select
            && !(field.Options ?? []).Any(option => string.Equals(option.Value, value, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Setting '{field.Key}' is not one of its declared options.", nameof(value));
        }
    }
}

internal sealed class LocalPackageFileStore(string rootPath) : IPackageFileStore
{
    private readonly string _rootPath = Path.GetFullPath(rootPath);

    public async Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var path = PackageWorkspacePath.Resolve(_rootPath, relativePath);
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        var path = PackageWorkspacePath.Resolve(_rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, contents.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(PackageWorkspacePath.Resolve(_rootPath, relativePath));
        return Task.CompletedTask;
    }

    internal string ResolvePath(string relativePath) => PackageWorkspacePath.Resolve(_rootPath, relativePath);
}

internal sealed class LocalPackageRoleLocalWorkspace(string rootPath) : IPackageRoleLocalWorkspace
{
    public string WorkspaceRootPath { get; } = Path.GetFullPath(rootPath);

    public string GetLocalPath(string relativePath)
        => PackageWorkspacePath.Resolve(WorkspaceRootPath, relativePath);

}

internal static class PackageWorkspacePath
{
    internal static string Resolve(string rootPath, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Package workspace paths must be relative.", nameof(relativePath));
        }

        var segments = relativePath.Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Package workspace paths must not be empty or contain traversal segments.", nameof(relativePath));
        }

        var canonicalRoot = Path.GetFullPath(rootPath);
        EnsureNoReparsePoints(canonicalRoot, segments);
        var fullPath = Path.GetFullPath(Path.Combine([canonicalRoot, .. segments]));
        var rootPrefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Package workspace paths must remain inside the workspace.", nameof(relativePath));
        }

        return fullPath;
    }

    private static void EnsureNoReparsePoints(string rootPath, IReadOnlyList<string> segments)
    {
        var current = rootPath;
        EnsureNotReparsePoint(current);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            EnsureNotReparsePoint(current);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null
                || new DirectoryInfo(path).LinkTarget is not null
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("Package workspace paths must not traverse symbolic links or reparse points.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
