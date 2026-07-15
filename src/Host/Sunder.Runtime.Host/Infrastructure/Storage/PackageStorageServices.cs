using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Settings;

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

internal interface IPackageSettingsDocument : IPackageSettings
{
    Task ReplaceValuesAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default);
}

internal sealed class PackageSettings(JsonPackageKeyValueStore store) : IPackageSettingsDocument
{
    internal PackageSettingsSchema? Schema { private get; set; }

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

    public Task ReplaceValuesAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
        => store.ReplaceValuesAsync(values, cancellationToken);

    private PackageSettingsField GetField(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var field = Schema?.Sections
            .SelectMany(section => section.Fields)
            .FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));
        if (field is null)
        {
            throw new ArgumentException($"Setting '{key}' is not declared by the package settings schema.", nameof(key));
        }

        if (field.Kind == PackageSettingsFieldKind.Secret)
        {
            throw new ArgumentException($"Setting '{key}' is secret and must be accessed through package secrets.", nameof(key));
        }

        return field;
    }

    internal static void ValidateValue(PackageSettingsField field, string? value)
    {
        var effectiveValue = value ?? field.DefaultValue;
        if (field.IsRequired && string.IsNullOrWhiteSpace(effectiveValue))
        {
            throw new ArgumentException($"Setting '{field.Key}' requires a value.", nameof(value));
        }

        if (effectiveValue is null)
        {
            return;
        }

        if (field.Kind == PackageSettingsFieldKind.Boolean && !bool.TryParse(effectiveValue, out _))
        {
            throw new ArgumentException($"Setting '{field.Key}' must be 'true' or 'false'.", nameof(value));
        }

        if (field.Kind == PackageSettingsFieldKind.Select
            && !field.Options.Any(option => string.Equals(option.Value, effectiveValue, StringComparison.Ordinal)))
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

    public ValueTask<Stream?> OpenReadAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PackageWorkspacePath.Resolve(_rootPath, relativePath);
        Stream? stream = File.Exists(path)
            ? new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan)
            : null;
        return ValueTask.FromResult(stream);
    }

    public async Task WriteAsync(
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        await ReplaceAsync(
            relativePath,
            (stream, token) => stream.WriteAsync(contents, token).AsTask(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(
        string relativePath,
        Stream contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        if (!contents.CanRead)
        {
            throw new ArgumentException("Package file content stream must be readable.", nameof(contents));
        }

        await ReplaceAsync(
            relativePath,
            (stream, token) => contents.CopyToAsync(stream, token),
            cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(PackageWorkspacePath.Resolve(_rootPath, relativePath));
        return Task.CompletedTask;
    }

    internal string ResolvePath(string relativePath) => PackageWorkspacePath.Resolve(_rootPath, relativePath);

    private async Task ReplaceAsync(
        string relativePath,
        Func<FileStream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PackageWorkspacePath.Resolve(_rootPath, relativePath);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await writeAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
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
