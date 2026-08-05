using System.Text.Json;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Tasks;

internal static class PackageManifestSerializer
{
    public static void Write(string outputPath, SunderPackageManifest manifest, JsonSerializerOptions options)
    {
        var temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, options) + "\n");
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
