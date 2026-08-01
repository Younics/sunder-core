using System.Runtime.InteropServices;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal static class PackageTargetSelection
{
    public static string GetCurrentRuntimeIdentifier()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        if (!SunderPackageFormat.IsRuntimeIdentifier(rid))
        {
            throw new PlatformNotSupportedException(
                $"The Runtime host RID '{rid}' is unsupported. Supported exact RIDs: {string.Join(", ", SunderPackageFormat.SupportedRuntimeIdentifiers)}.");
        }
        return rid;
    }

    public static PackageHostRoles GetHostRoles(SunderPackageManifest manifest)
    {
        var roles = PackageHostRoles.None;
        foreach (var key in SunderPackageTargetResolver.EnumerateTargets(manifest))
        {
            roles |= key.Role switch
            {
                SunderPackageFormat.AppHostRole => PackageHostRoles.App,
                SunderPackageFormat.RuntimeHostRole => PackageHostRoles.Runtime,
                _ => throw new InvalidDataException($"Package manifest contains unknown target role '{key.Role}'."),
            };
        }
        return roles;
    }

    public static PackageTargetDescriptor ToDescriptor(SunderPackageTargetManifest target)
        => new(
            target.Role!,
            target.Rid!,
            target.Kind!,
            target.EntryPoint!,
            target.TargetFramework,
            target.SdkVersion,
            (target.RequiredHostCapabilities ?? []).Select(static capability => capability!).ToArray(),
            (target.Views ?? []).Where(static view => view is not null).Select(static view => new PackageWebViewDescriptor(
                view!.ViewId!,
                view.DisplayName!,
                view.Route!,
                view.Icon,
                view.DefaultPlacement!,
                view.ShowInHotbar!.Value)).ToArray());
}
