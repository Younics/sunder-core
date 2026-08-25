using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Storage;

namespace Sunder.Sdk.Worker.Internal;

internal sealed class WorkerPackageSettings(WorkerRpcClient client) : IPackageSettings
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        WorkerPackageDataValidation.ValidateKey(key, nameof(key));
        return WorkerPackageDataValidation.TranslateValidationAsync(
            client.GetSettingAsync(key, storedOnly: false, cancellationToken),
            nameof(key));
    }

    public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
    {
        WorkerPackageDataValidation.ValidateKey(key, nameof(key));
        return WorkerPackageDataValidation.TranslateValidationAsync(
            client.GetSettingAsync(key, storedOnly: true, cancellationToken),
            nameof(key));
    }

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        WorkerPackageDataValidation.ValidateKey(key, nameof(key));
        WorkerPackageDataValidation.ValidateValue(value, nameof(value));
        return WorkerPackageDataValidation.TranslateValidationAsync(
            client.SetSettingAsync(key, value, cancellationToken),
            nameof(value));
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        WorkerPackageDataValidation.ValidateKey(key, nameof(key));
        return WorkerPackageDataValidation.TranslateValidationAsync(
            client.DeleteSettingAsync(key, cancellationToken),
            nameof(key));
    }
}

internal sealed class WorkerPackageKeyValueStore(WorkerRpcClient client) : IPackageKeyValueStore, IPackageStorageKeyMigrator
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        WorkerPackageDataValidation.ValidateKey(key, nameof(key));
        return WorkerPackageDataValidation.TranslateValidationAsync(
            client.GetStateAsync(key, cancellationToken),
            nameof(key));
    }

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        WorkerPackageDataValidation.ValidateKey(key, nameof(key));
        WorkerPackageDataValidation.ValidateValue(value, nameof(value));
        return WorkerPackageDataValidation.TranslateValidationAsync(
            client.SetStateAsync(key, value, cancellationToken),
            nameof(value));
    }

    public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        WorkerPackageDataValidation.ValidateKey(key, nameof(key));
        return WorkerPackageDataValidation.TranslateValidationAsync(
            client.ContainsStateKeyAsync(key, cancellationToken),
            nameof(key));
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        WorkerPackageDataValidation.ValidateKey(key, nameof(key));
        return WorkerPackageDataValidation.TranslateValidationAsync(
            client.DeleteStateAsync(key, cancellationToken),
            nameof(key));
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        WorkerPackageDataValidation.ValidatePrefix(prefix, nameof(prefix));
        return WorkerPackageDataValidation.TranslateValidationAsync(
            client.ListStateKeysAsync(prefix, cancellationToken),
            nameof(prefix));
    }

    public Task MigrateKeysAsync(
        IReadOnlyList<PackageStorageKeyMigration> migrations,
        CancellationToken cancellationToken = default)
        => client.MigrateStorageKeysAsync("state", migrations, cancellationToken);
}

internal sealed class WorkerPackageSecrets(WorkerRpcClient client) : IPackageSecrets, IPackageStorageKeyMigrator
{
    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        WorkerPackageDataValidation.ValidateKey(key, nameof(key));
        return WorkerPackageDataValidation.TranslateValidationAsync(
            client.GetSecretAsync(key, cancellationToken),
            nameof(key));
    }

    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        WorkerPackageDataValidation.ValidateKey(key, nameof(key));
        WorkerPackageDataValidation.ValidateValue(value, nameof(value));
        return WorkerPackageDataValidation.TranslateValidationAsync(
            client.SetSecretAsync(key, value, cancellationToken),
            nameof(value));
    }

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        WorkerPackageDataValidation.ValidateKey(key, nameof(key));
        return WorkerPackageDataValidation.TranslateValidationAsync(
            client.DeleteSecretAsync(key, cancellationToken),
            nameof(key));
    }

    public Task MigrateKeysAsync(
        IReadOnlyList<PackageStorageKeyMigration> migrations,
        CancellationToken cancellationToken = default)
        => client.MigrateStorageKeysAsync("secrets", migrations, cancellationToken);
}

internal sealed class WorkerPackageFileStore(WorkerRpcClient client) : IPackageFileStore
{
    public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        WorkerPackageDataValidation.ValidatePath(relativePath, nameof(relativePath));
        return WorkerPackageDataValidation.TranslateValidationAsync(
            client.ReadPackageFileAsync(relativePath, cancellationToken),
            nameof(relativePath));
    }

    public Task WriteAsync(
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        WorkerPackageDataValidation.ValidatePath(relativePath, nameof(relativePath));
        if (!PackageStorageValidation.IsValidFileLength(contents.Length))
        {
            throw new ArgumentException(
                $"Package files cannot exceed {PackageStorageValidation.MaximumFileBytes} bytes.",
                nameof(contents));
        }
        return WorkerPackageDataValidation.TranslateValidationAsync(
            client.WritePackageFileAsync(relativePath, contents, cancellationToken),
            nameof(relativePath));
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        WorkerPackageDataValidation.ValidatePath(relativePath, nameof(relativePath));
        return WorkerPackageDataValidation.TranslateValidationAsync(
            client.DeletePackageFileAsync(relativePath, cancellationToken),
            nameof(relativePath));
    }
}

internal sealed class WorkerPackageStorageContext : IPackageStorageContext
{
    public WorkerPackageStorageContext(WorkerRpcClient client, WorkerEnvironment environment)
    {
        Files = new WorkerPackageFileStore(client);
        State = new WorkerPackageKeyValueStore(client);
        RoleLocalWorkspace = new WorkerPackageRoleLocalWorkspace(
            environment.PackageWorkspacePath
            ?? throw new InvalidOperationException("Worker V3 persistent workspace is unavailable."));
    }

    public IPackageFileStore Files { get; }

    public IPackageKeyValueStore State { get; }

    public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; }
}

internal sealed class WorkerPackageRoleLocalWorkspace : IPackageRoleLocalWorkspace
{
    public WorkerPackageRoleLocalWorkspace(string rootPath)
        => WorkspaceRootPath = Path.GetFullPath(rootPath);

    public string WorkspaceRootPath { get; }

    public string GetLocalPath(string relativePath)
    {
        WorkerPackageDataValidation.ValidateWorkspacePath(relativePath, nameof(relativePath));

        var segments = relativePath.Split('/');
        var current = WorkspaceRootPath;
        EnsureNotLink(current, nameof(relativePath));
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            EnsureNotLink(current, nameof(relativePath));
        }

        var fullPath = Path.GetFullPath(Path.Combine([WorkspaceRootPath, .. segments]));
        var rootPrefix = Path.EndsInDirectorySeparator(WorkspaceRootPath)
            ? WorkspaceRootPath
            : WorkspaceRootPath + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(
                rootPrefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Package workspace paths must remain inside the persistent workspace.",
                nameof(relativePath));
        }
        return fullPath;
    }

    private static void EnsureNotLink(string path, string parameterName)
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
