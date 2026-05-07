using Sunder.Protocol;

namespace Sunder.App.Services;

public sealed class PackageRuntimeFaultReporter(IRuntimeApiClientFactory runtimeApiClientFactory)
{
    private readonly IRuntimeApiClientFactory _runtimeApiClientFactory = runtimeApiClientFactory;

    public void ReportPackageFault(string packageId, PackageFailureOrigin origin, string message)
    {
        _ = ReportPackageFaultAsync(packageId, origin, message);
    }

    private async Task ReportPackageFaultAsync(string packageId, PackageFailureOrigin origin, string message)
    {
        try
        {
            using var runtimeApiClient = _runtimeApiClientFactory.CreateClient();
            await runtimeApiClient.ReportPackageFaultAsync(packageId, origin, message);
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError($"Failed to report package fault for '{packageId}'.", ex);
        }
    }
}
