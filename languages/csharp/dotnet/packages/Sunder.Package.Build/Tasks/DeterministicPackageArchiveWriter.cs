using Sunder.Package.Format;

namespace Sunder.Package.Build.Tasks;

internal static class DeterministicPackageArchiveWriter
{
    public static void Write(string sourcePath, string outputPath)
    {
        var temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            SunderArchive.WriteDeterministic(sourcePath, temporaryPath);
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
