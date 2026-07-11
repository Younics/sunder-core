using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed class PackageRuntimeFaultReporter(IRuntimeApiClientFactory runtimeApiClientFactory) : IDisposable
{
    private readonly IRuntimeApiClientFactory _runtimeApiClientFactory = runtimeApiClientFactory;
    private readonly OwnedTaskObserver _tasks = new(nameof(PackageRuntimeFaultReporter));

    public void ReportPackageFault(string packageId, PackageFailureOrigin origin, string message)
    {
        _tasks.Observe(ReportPackageFaultAsync(packageId, origin, message), $"reporting package fault for '{packageId}'");
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

    public void Dispose() => _tasks.Dispose();
}
