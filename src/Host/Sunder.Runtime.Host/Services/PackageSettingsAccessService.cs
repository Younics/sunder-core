using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSettingsAccessService(RuntimeSessionOwner sessions)
{
    private readonly PackageSettingsService _settings = new();

    public IReadOnlyList<PackageConfigurationSchemaDescriptor> GetSchemas()
    {
        using var lease = sessions.State.AcquireLease();
        return _settings.GetSchemas(sessions.State.ListEnabledLoadedPackages(lease));
    }

    public async Task<PackageSettingsValuesResponse?> GetValuesAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        using var lease = sessions.State.AcquireLease();
        var package = sessions.State.GetLoadedPackage(lease, packageId);
        return package is null
            ? null
            : await _settings.GetValuesAsync(package, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SaveValuesAsync(
        string packageId,
        UpdatePackageSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        using var lease = sessions.State.AcquireLease();
        var package = sessions.State.GetLoadedPackage(lease, packageId);
        if (package is null)
        {
            return false;
        }

        await _settings.SaveValuesAsync(package, request, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<PackageSettingValueResponse?> GetValueAsync(
        string packageId,
        string key,
        CancellationToken cancellationToken = default)
    {
        using var lease = sessions.State.AcquireLease();
        var package = sessions.State.GetLoadedPackage(lease, packageId);
        if (package is null)
        {
            return null;
        }

        try
        {
            var settings = package.Settings;
            var stored = await settings.GetStoredValueAsync(key, cancellationToken).ConfigureAwait(false);
            var effective = stored ?? await settings.GetValueAsync(key, cancellationToken).ConfigureAwait(false);
            return new PackageSettingValueResponse(stored is not null, stored, effective);
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
        var package = sessions.State.GetLoadedPackage(lease, packageId);
        if (package is null)
        {
            return false;
        }

        try
        {
            var settings = package.Settings;
            await settings.SetValueAsync(key, value, cancellationToken).ConfigureAwait(false);
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
        var package = sessions.State.GetLoadedPackage(lease, packageId);
        if (package is null)
        {
            return false;
        }

        try
        {
            var settings = package.Settings;
            await settings.DeleteValueAsync(key, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ArgumentException exception)
        {
            throw new RuntimeValidationException(exception.Message);
        }
    }
}
