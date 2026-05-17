using Sunder.Protocol;

namespace Sunder.App.ViewModels;

internal static class PackageIconUriResolver
{
    public static Uri? Resolve(
        string packageId,
        PackageIconDescriptor? icon,
        Func<string, string, Uri> createPackageAssetUri)
    {
        if (string.IsNullOrWhiteSpace(icon?.AssetPath))
        {
            return null;
        }

        try
        {
            return createPackageAssetUri(packageId, icon.AssetPath!);
        }
        catch
        {
            return null;
        }
    }
}
