using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Sunder.Package.Build.Tasks;

public sealed class PackSunderPackageTask : Microsoft.Build.Utilities.Task
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Required]
    public string DevPackagePath { get; set; } = string.Empty;

    [Required]
    public string PackageOutputPath { get; set; } = string.Empty;

    public override bool Execute()
    {
        try
        {
            if (!Directory.Exists(DevPackagePath))
            {
                Log.LogError($"Sunder dev package folder '{DevPackagePath}' does not exist.");
                return false;
            }

            var manifestPath = Path.Combine(DevPackagePath, "sunder-package.json");
            if (!File.Exists(manifestPath))
            {
                Log.LogError($"Sunder dev package folder '{DevPackagePath}' does not contain sunder-package.json.");
                return false;
            }

            var stagingPath = Path.Combine(Path.GetTempPath(), "Sunder.Package.Build", "pack", Guid.NewGuid().ToString("N"));
            try
            {
                BuildPackageLayout(stagingPath, manifestPath);
                WriteContentIndex(stagingPath);

                var packageOutputDirectory = Path.GetDirectoryName(PackageOutputPath);
                if (!string.IsNullOrWhiteSpace(packageOutputDirectory))
                {
                    Directory.CreateDirectory(packageOutputDirectory);
                }

                if (File.Exists(PackageOutputPath))
                {
                    File.Delete(PackageOutputPath);
                }

                ZipFile.CreateFromDirectory(stagingPath, PackageOutputPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                Log.LogMessage(MessageImportance.High, $"Packed Sunder package to {PackageOutputPath}");
                return true;
            }
            finally
            {
                TryDeleteDirectory(stagingPath);
            }
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: false);
            return false;
        }
    }

    private void BuildPackageLayout(string stagingPath, string manifestPath)
    {
        var manifestOutputPath = Path.Combine(stagingPath, "manifest", "sunder-package.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestOutputPath)!);
        File.Copy(manifestPath, manifestOutputPath, overwrite: true);

        CopyDirectoryIfExists(Path.Combine(DevPackagePath, "lib"), Path.Combine(stagingPath, "payload", "lib"));
        CopyDirectoryIfExists(Path.Combine(DevPackagePath, "assets"), Path.Combine(stagingPath, "payload", "assets"));
    }

    private static void CopyDirectoryIfExists(string sourcePath, string destinationPath)
    {
        if (!Directory.Exists(sourcePath))
        {
            return;
        }

        foreach (var sourceFile in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourcePath, sourceFile);
            var destinationFile = Path.Combine(destinationPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: true);
        }
    }

    private static void WriteContentIndex(string stagingPath)
    {
        var files = Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories)
            .Where(static path => !string.Equals(Path.GetFileName(path), "content-index.json", StringComparison.OrdinalIgnoreCase))
            .Select(path => CreateIndexEntry(stagingPath, path))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var index = new SunderPackageContentIndex(1, files);
        var outputPath = Path.Combine(stagingPath, "manifest", "content-index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(index, JsonOptions) + Environment.NewLine);
    }

    private static SunderPackageContentIndexEntry CreateIndexEntry(string stagingPath, string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        var relativePath = Path.GetRelativePath(stagingPath, filePath).Replace('\\', '/');
        return new SunderPackageContentIndexEntry(
            relativePath,
            Convert.ToHexString(hash).ToLowerInvariant(),
            stream.Length,
            ResolveRole(relativePath));
    }

    private static string ResolveRole(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        if (relativePath.StartsWith("payload/lib/runtimes/", StringComparison.OrdinalIgnoreCase))
        {
            return "native";
        }

        if (relativePath.StartsWith("payload/lib/", StringComparison.OrdinalIgnoreCase)
            && string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase))
        {
            return "assembly";
        }

        if (relativePath.StartsWith("payload/assets/", StringComparison.OrdinalIgnoreCase))
        {
            return "asset";
        }

        if (relativePath.StartsWith("manifest/", StringComparison.OrdinalIgnoreCase))
        {
            return "manifest";
        }

        return "file";
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup for temporary package staging.
        }
    }

    private sealed record SunderPackageContentIndex(int SchemaVersion, IReadOnlyList<SunderPackageContentIndexEntry> Files);

    private sealed record SunderPackageContentIndexEntry(string Path, string Sha256, long Size, string Role);
}
