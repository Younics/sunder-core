using Sunder.Package.Format;

namespace Sunder.Runtime.Host.Services;

internal static class PackageManifestAdmissionPolicy
{
    public static void EnsureSupported(SunderPackageManifest manifest)
    {
        // Shape is shared with archive validation. Complete authority and reviewed
        // installation constraints are checked by PackageCompatibilityAuthority.
        var errors = new List<string>();
        PackageRpcImportValidator.Validate(manifest, errors);
        foreach (var dependency in manifest.DependsOn ?? [])
        {
            if (dependency is null || dependency.Presence is not ("required" or "optional")) errors.Add("Dependencies must explicitly declare required or optional presence.");
        }
        if (errors.Count > 0) throw new InvalidDataException(string.Join(" | ", errors));
    }
}
