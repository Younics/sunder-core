using Sunder.Runtime.Contracts;
using static Sunder.Runtime.Host.Services.PackageProtocolMapper;

namespace Sunder.Runtime.Host.Services;

internal static class RuntimePackageRoleActivation
{
    public static bool TryCompleteWithoutRuntime(
        PreparedRuntimePackage package,
        ISet<string> readyPackageIds,
        ICollection<RuntimePackageSource> readySources,
        IDictionary<string, SessionPackageDescriptor> sessionPackages)
    {
        if ((package.HostRoles & PackageHostRoles.Runtime) != 0) return false;
        MarkReady(package, readyPackageIds, readySources);
        sessionPackages[package.PackageId] = BuildSessionDescriptor(
            package.Activation,
            isEnabled: true,
            readiness: PackageReadinessState.Ready);
        return true;
    }

    public static void MarkReady(
        PreparedRuntimePackage package,
        ISet<string> readyPackageIds,
        ICollection<RuntimePackageSource> readySources)
    {
        readyPackageIds.Add(package.PackageId);
        readySources.Add(package.Source);
    }
}
