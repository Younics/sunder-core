using System.Runtime.InteropServices;
using Sunder.Package.Format;

namespace Sunder.App.Services;

internal static class AppPackageTargetEnvironment
{
    public static string CurrentRid { get; } = Resolve(RuntimeInformation.RuntimeIdentifier);

    internal static string Resolve(string runtimeIdentifier)
    {
        if (!SunderPackageFormat.IsRuntimeIdentifier(runtimeIdentifier))
        {
            throw new PlatformNotSupportedException(
                $"The App RID '{runtimeIdentifier}' is unsupported. Supported exact RIDs: {string.Join(", ", SunderPackageFormat.SupportedRuntimeIdentifiers)}.");
        }
        return runtimeIdentifier;
    }
}
