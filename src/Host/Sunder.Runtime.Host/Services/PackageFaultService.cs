using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageFaultService(RuntimeSessionOwner sessions)
{
    public bool Report(string packageId, ReportPackageFaultRequest request)
        => sessions.State.ReportPackageFault(packageId, request);
}
