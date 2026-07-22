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
        using var lease = sessionState.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var package = sessionState.GetLoadedPackage(lease, packageId);
        if (package is null)
        {
            return null;
        }

        var value = await package.StateStore.GetValueAsync(key, linked.Token);
        return new PackageDataValueResponse(value is not null, value);
    }

    public async Task<IReadOnlyList<string>?> ListStateKeysAsync(
        string packageId,
        string? prefix,
        CancellationToken cancellationToken)
    {
        using var lease = sessionState.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var package = sessionState.GetLoadedPackage(lease, packageId);
        return package is null
            ? null
            : await package.StateStore.ListKeysAsync(prefix, linked.Token);
    }

    public async Task<bool> SetStateAsync(
        string packageId,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        using var lease = sessionState.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var package = sessionState.GetLoadedPackage(lease, packageId);
        if (package is null)
        {
            return false;
        }

        await package.StateStore.SetValueAsync(key, value, linked.Token);
        return true;
    }

    public async Task<bool> DeleteStateAsync(string packageId, string key, CancellationToken cancellationToken)
    {
        using var lease = sessionState.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var package = sessionState.GetLoadedPackage(lease, packageId);
        if (package is null)
        {
            return false;
        }

        await package.StateStore.DeleteValueAsync(key, linked.Token);
        return true;
    }

    public async Task<PackageDataValueResponse?> GetSecretAsync(
        string packageId,
        string key,
        CancellationToken cancellationToken)
    {
        using var lease = sessionState.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var package = sessionState.GetLoadedPackage(lease, packageId);
        if (package is null)
        {
            return null;
        }

        var value = await package.SecretsStore.GetSecretAsync(key, linked.Token);
        return new PackageDataValueResponse(value is not null, value);
    }

    public async Task<bool> SetSecretAsync(
        string packageId,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        using var lease = sessionState.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var package = sessionState.GetLoadedPackage(lease, packageId);
        if (package is null)
        {
            return false;
        }

        await package.SecretsStore.SetSecretAsync(key, value, linked.Token);
        return true;
    }

    public async Task<bool> DeleteSecretAsync(string packageId, string key, CancellationToken cancellationToken)
    {
        using var lease = sessionState.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var package = sessionState.GetLoadedPackage(lease, packageId);
        if (package is null)
        {
            return false;
        }

        await package.SecretsStore.DeleteSecretAsync(key, linked.Token);
        return true;
    }

    public async Task<byte[]?> ReadFileAsync(
        string packageId,
        string relativePath,
        int maxLength,
        CancellationToken cancellationToken)
    {
        using var lease = sessionState.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var files = TryGetFileStore(lease, packageId);
        if (files is null)
        {
            return null;
        }

        var contents = await files.ReadAsync(relativePath, linked.Token).ConfigureAwait(false);
        if (contents?.Length > maxLength)
        {
            throw new InvalidDataException("The package file exceeds the Runtime read limit.");
        }

        return contents;
    }

    public async Task<bool> WriteFileAsync(
        string packageId,
        string relativePath,
        byte[] contents,
        CancellationToken cancellationToken)
    {
        using var lease = sessionState.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var files = TryGetFileStore(lease, packageId);
        if (files is null)
        {
            return false;
        }

        await files.WriteAsync(relativePath, contents, linked.Token).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DeleteFileAsync(
        string packageId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        using var lease = sessionState.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var files = TryGetFileStore(lease, packageId);
        if (files is null)
        {
            return false;
        }

        await files.DeleteAsync(relativePath, linked.Token).ConfigureAwait(false);
        return true;
    }

    private LocalPackageFileStore? TryGetFileStore(PackageSessionLease lease, string packageId)
    {
        var package = sessionState.GetLoadedPackage(lease, packageId);
        var context = package?.ServiceProvider.GetService(typeof(Sunder.Sdk.Abstractions.IPackageContext))
            as RuntimePackageContext;
        return context?.LocalStorage.Files as LocalPackageFileStore;
    }

}
