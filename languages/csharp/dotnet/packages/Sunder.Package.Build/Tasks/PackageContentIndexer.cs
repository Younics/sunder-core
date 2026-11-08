using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Tasks;

internal static class PackageContentIndexer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static void Write(string stagingPath)
    {
        var files = SunderArchive.EnumerateFiles(stagingPath)
            .Where(file => !SunderPackageFormat.IsContentIndexPath(file.Path.ToString()))
            .Select(file => CreateEntry(file.Path, file.FullPath))
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        foreach (var entry in files)
        {
            if (!SunderPackageFormat.IsAllowedArchivePath(entry.Path!))
            {
                throw new InvalidDataException($"Package staging contains file outside canonical archive roots: '{entry.Path}'.");
            }
        }

        var outputPath = ArchiveRelativePath.Parse(SunderPackageFormat.ContentIndexPath).ToPlatformPath(stagingPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    new SunderPackageContentIndex(SunderPackageFormat.CurrentContentIndexVersion, files),
                    JsonOptions) + "\n");
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static SunderPackageContentIndexEntry CreateEntry(ArchiveRelativePath path, string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return new SunderPackageContentIndexEntry(
            path.ToString(),
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
            stream.Length);
    }
}
