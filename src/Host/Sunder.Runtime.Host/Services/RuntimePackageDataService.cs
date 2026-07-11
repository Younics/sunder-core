using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageDataService(PackageSessionState sessionState)
{
    public async Task<PackageDataValueResponse?> GetStateAsync(
        string packageId,
        string key,
        CancellationToken cancellationToken)
    {
        var package = sessionState.GetLoadedPackage(packageId);
        if (package is null)
        {
            return null;
        }

        var value = await package.StateStore.GetValueAsync(key, cancellationToken);
        return new PackageDataValueResponse(value is not null, value);
    }

    public async Task<IReadOnlyList<string>?> ListStateKeysAsync(
        string packageId,
        string? prefix,
        CancellationToken cancellationToken)
    {
        var package = sessionState.GetLoadedPackage(packageId);
        return package is null
            ? null
            : await package.StateStore.ListKeysAsync(prefix, cancellationToken);
    }

    public async Task<bool> SetStateAsync(
        string packageId,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        var package = sessionState.GetLoadedPackage(packageId);
        if (package is null)
        {
            return false;
        }

        await package.StateStore.SetValueAsync(key, value, cancellationToken);
        return true;
    }

    public async Task<bool> DeleteStateAsync(string packageId, string key, CancellationToken cancellationToken)
    {
        var package = sessionState.GetLoadedPackage(packageId);
        if (package is null)
        {
            return false;
        }

        await package.StateStore.DeleteValueAsync(key, cancellationToken);
        return true;
    }

    public async Task<PackageDataValueResponse?> GetSecretAsync(
        string packageId,
        string key,
        CancellationToken cancellationToken)
    {
        var package = sessionState.GetLoadedPackage(packageId);
        if (package is null)
        {
            return null;
        }

        var value = await package.SecretsStore.GetSecretAsync(key, cancellationToken);
        return new PackageDataValueResponse(value is not null, value);
    }

    public async Task<bool> SetSecretAsync(
        string packageId,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        var package = sessionState.GetLoadedPackage(packageId);
        if (package is null)
        {
            return false;
        }

        await package.SecretsStore.SetSecretAsync(key, value, cancellationToken);
        return true;
    }

    public async Task<bool> DeleteSecretAsync(string packageId, string key, CancellationToken cancellationToken)
    {
        var package = sessionState.GetLoadedPackage(packageId);
        if (package is null)
        {
            return false;
        }

        await package.SecretsStore.DeleteSecretAsync(key, cancellationToken);
        return true;
    }

    public async Task<byte[]?> ReadFileAsync(
        string packageId,
        string relativePath,
        int maxLength,
        CancellationToken cancellationToken)
    {
        var filePath = TryGetFilePath(packageId, relativePath);
        if (filePath is null || !File.Exists(filePath))
        {
            return null;
        }

        if (new FileInfo(filePath).Length > maxLength)
        {
            throw new InvalidDataException("The package file exceeds the Runtime read limit.");
        }

        return await File.ReadAllBytesAsync(filePath, cancellationToken);
    }

    public async Task<bool> WriteFileAsync(
        string packageId,
        string relativePath,
        byte[] contents,
        CancellationToken cancellationToken)
    {
        var filePath = TryGetFilePath(packageId, relativePath);
        if (filePath is null)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, contents, cancellationToken);
        return true;
    }

    public Task<bool> DeleteFileAsync(string packageId, string relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filePath = TryGetFilePath(packageId, relativePath);
        if (filePath is null)
        {
            return Task.FromResult(false);
        }

        File.Delete(filePath);
        return Task.FromResult(true);
    }

    private string? TryGetFilePath(string packageId, string relativePath)
    {
        var package = sessionState.GetLoadedPackage(packageId);
        var context = package?.ServiceProvider.GetService(typeof(Sunder.Sdk.Abstractions.IPackageContext))
            as RuntimePackageContext;
        return context?.LocalStorage is { Files: LocalPackageFileStore files }
            ? files.ResolvePath(relativePath)
            : null;
    }

}
