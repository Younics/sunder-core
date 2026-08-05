using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSettingsAccessService(RuntimeSessionOwner sessions)
{
    private readonly PackageSettingsService _settings = new();

    public IReadOnlyList<PackageSettingsSchemaDescriptor> GetSchemas()
    {
        using var lease = sessions.State.AcquireLease();
        return _settings.GetSchemas(sessions.State.ListEnabledLoadedPackages(lease));
    }

    public async Task<PackageSettingsValuesResponse?> GetValuesAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        using var lease = sessions.State.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var package = sessions.State.GetLoadedPackage(lease, packageId);
        return package is null
            ? null
            : await _settings.GetValuesAsync(package, linked.Token).ConfigureAwait(false);
    }

    public async Task<bool> SaveValuesAsync(
        string packageId,
        UpdatePackageSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        using var lease = sessions.State.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var package = sessions.State.GetLoadedPackage(lease, packageId);
        if (package is null)
        {
            return false;
        }

        await _settings.SaveValuesAsync(package, request, linked.Token).ConfigureAwait(false);
        return true;
    }

    public async Task<PackageSettingValueResponse?> GetValueAsync(
        string packageId,
        string key,
        CancellationToken cancellationToken = default)
    {
        using var lease = sessions.State.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var package = sessions.State.GetLoadedPackage(lease, packageId);
        if (package is null)
        {
            return null;
        }

        try
        {
            return await _settings.GetValueAsync(package, key, linked.Token).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            throw new RuntimeValidationException(exception.Message);
        }
    }

    public async Task<bool> SetValueAsync(
        string packageId,
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        using var lease = sessions.State.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var package = sessions.State.GetLoadedPackage(lease, packageId);
        if (package is null)
        {
            return false;
        }

        try
        {
            await _settings.SetValueAsync(package, key, value, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (ArgumentException exception)
        {
            throw new RuntimeValidationException(exception.Message);
        }
    }

    public async Task<bool> DeleteValueAsync(
        string packageId,
        string key,
        CancellationToken cancellationToken = default)
    {
        using var lease = sessions.State.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var package = sessions.State.GetLoadedPackage(lease, packageId);
        if (package is null)
        {
            return false;
        }

        try
        {
            await _settings.DeleteValueAsync(package, key, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (ArgumentException exception)
        {
            throw new RuntimeValidationException(exception.Message);
        }
    }
}
