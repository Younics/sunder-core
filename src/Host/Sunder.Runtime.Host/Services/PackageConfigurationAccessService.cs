using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageConfigurationAccessService(RuntimeSessionOwner sessions)
{
    private readonly PackageConfigurationService _configuration = new();

    public IReadOnlyList<PackageConfigurationSchemaDescriptor> GetSchemas()
        => _configuration.GetConfigurationSchemas(sessions.State.ListEnabledLoadedPackages());

    public async Task<PackageConfigurationValuesResponse?> GetValuesAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        var (package, generation) = sessions.State.GetLoadedPackageLease(packageId);
        if (package is null)
        {
            return null;
        }

        try
        {
            return await _configuration.GetConfigurationValuesAsync(package, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            sessions.State.HandlePackageFault(packageId, generation, PackageFailureOrigin.RuntimeConfiguration, exception, "load package configuration");
            return null;
        }
    }

    public async Task<bool> SaveValuesAsync(
        string packageId,
        UpdatePackageConfigurationValuesRequest request,
        CancellationToken cancellationToken = default)
    {
        var (package, generation) = sessions.State.GetLoadedPackageLease(packageId);
        if (package?.ConfigurationSchema is null)
        {
            return false;
        }

        try
        {
            return await _configuration.SaveConfigurationValuesAsync(package, request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            sessions.State.HandlePackageFault(packageId, generation, PackageFailureOrigin.RuntimeConfiguration, exception, "save package configuration");
            return false;
        }
    }
}
