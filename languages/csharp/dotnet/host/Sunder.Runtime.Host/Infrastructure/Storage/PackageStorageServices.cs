using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Storage;

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
        PackageStorageGuards.Key(key, nameof(key));
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

        PackageStorageGuards.Value(effectiveValue, nameof(value));

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
        await using var stream = await OpenReadAsync(relativePath, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }

        using var contents = new MemoryStream(stream.CanSeek ? checked((int)stream.Length) : 0);
        await CopyReadAsync(stream, contents, cancellationToken).ConfigureAwait(false);
        return contents.ToArray();
    }

    public ValueTask<Stream?> OpenReadAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PackageWorkspacePath.Resolve(_rootPath, relativePath);
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return ValueTask.FromResult<Stream?>(null);
        }
        catch (DirectoryNotFoundException)
        {
            return ValueTask.FromResult<Stream?>(null);
        }

        try
        {
            if (!PackageStorageValidation.IsValidFileLength(stream.Length))
            {
                throw new InvalidDataException(
                    $"The package file exceeds the {PackageStorageValidation.MaximumFileBytes} byte limit.");
            }

            return ValueTask.FromResult<Stream?>(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public async Task WriteAsync(
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        PackageStorageGuards.RelativePath(relativePath, nameof(relativePath));
        PackageStorageGuards.FileLength(contents.Length, nameof(contents));
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
        PackageStorageGuards.RelativePath(relativePath, nameof(relativePath));
        if (!contents.CanRead)
        {
            throw new ArgumentException("Package file content stream must be readable.", nameof(contents));
        }
        if (contents.CanSeek
            && contents.Length - contents.Position > PackageStorageValidation.MaximumFileBytes)
        {
            throw new ArgumentException(
                $"Package files cannot exceed {PackageStorageValidation.MaximumFileBytes} bytes.",
                nameof(contents));
        }

        await ReplaceAsync(
            relativePath,
            (stream, token) => CopyWriteAsync(contents, stream, token),
            cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(PackageWorkspacePath.Resolve(_rootPath, relativePath));
        return Task.CompletedTask;
    }

    internal string ResolvePath(string relativePath) => PackageWorkspacePath.Resolve(_rootPath, relativePath);

    private static async Task CopyReadAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long totalBytes = 0;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return;
            }
            if (totalBytes + bytesRead > PackageStorageValidation.MaximumFileBytes)
            {
                throw new InvalidDataException(
                    $"The package file exceeds the {PackageStorageValidation.MaximumFileBytes} byte limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            totalBytes += bytesRead;
        }
    }

    private static async Task CopyWriteAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long totalBytes = 0;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return;
            }
            if (totalBytes + bytesRead > PackageStorageValidation.MaximumFileBytes)
            {
                throw new ArgumentException(
                    $"Package files cannot exceed {PackageStorageValidation.MaximumFileBytes} bytes.",
                    "contents");
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            totalBytes += bytesRead;
        }
    }

    private async Task ReplaceAsync(
        string relativePath,
        Func<FileStream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PackageWorkspacePath.Resolve(_rootPath, relativePath);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        path = PackageWorkspacePath.Resolve(_rootPath, relativePath);
        var temporaryPath = Path.Combine(directory, $".sunder-{Guid.NewGuid():N}.tmp");
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
            path = PackageWorkspacePath.Resolve(_rootPath, relativePath);
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
        PackageStorageGuards.RelativePath(relativePath, nameof(relativePath));
        var segments = relativePath.Split('/');

        var canonicalRoot = Path.GetFullPath(rootPath);
        EnsureNoReparsePoints(canonicalRoot, segments, nameof(relativePath));
        var fullPath = Path.GetFullPath(Path.Combine([canonicalRoot, .. segments]));
        var rootPrefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootPrefix, pathComparison))
        {
            throw new ArgumentException("Package workspace paths must remain inside the workspace.", nameof(relativePath));
        }

        return fullPath;
    }

    private static void EnsureNoReparsePoints(
        string rootPath,
        IReadOnlyList<string> segments,
        string parameterName)
    {
        var current = rootPath;
        EnsureNotReparsePoint(current, parameterName);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            EnsureNotReparsePoint(current, parameterName);
        }
    }

    private static void EnsureNotReparsePoint(string path, string parameterName)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null
                || new DirectoryInfo(path).LinkTarget is not null
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException(
                    "Package workspace paths must not traverse symbolic links or reparse points.",
                    parameterName);
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
