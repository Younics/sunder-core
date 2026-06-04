using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sunder.PackageManagement;

public static class SunderStackArchiveWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task WriteAsync(
        SunderStackManifest manifest,
        string stackOutputPath,
        IReadOnlyDictionary<string, string>? payloadFiles = null,
        CancellationToken cancellationToken = default)
    {
        var stagingPath = Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "pack", Guid.NewGuid().ToString("N"));
        try
        {
            await BuildStackLayoutAsync(stagingPath, manifest, payloadFiles ?? new Dictionary<string, string>(), cancellationToken);
            WriteContentIndex(stagingPath);
            WriteArchive(stagingPath, stackOutputPath);
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
    }

    public static async Task MergeAsync(
        string baseStackPath,
        string? preservedStackPath,
        SunderStackManifest manifest,
        string stackOutputPath,
        IReadOnlyList<string>? preservedArchivePaths = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(baseStackPath))
        {
            throw new FileNotFoundException($"Base Stack archive '{baseStackPath}' was not found.", baseStackPath);
        }

        var stagingPath = Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "merge", Guid.NewGuid().ToString("N"));
        var preservedStagingPath = Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "merge", Guid.NewGuid().ToString("N"));
        try
        {
            SunderStackArchiveInspector.ExtractArchive(baseStackPath, stagingPath);
            if (!string.IsNullOrWhiteSpace(preservedStackPath) && preservedArchivePaths?.Count > 0)
            {
                if (!File.Exists(preservedStackPath))
                {
                    throw new FileNotFoundException($"Preserved Stack archive '{preservedStackPath}' was not found.", preservedStackPath);
                }

                SunderStackArchiveInspector.ExtractArchive(preservedStackPath, preservedStagingPath);
                foreach (var archivePath in preservedArchivePaths
                             .Where(path => !string.IsNullOrWhiteSpace(path))
                             .Select(path => path.Replace('\\', '/'))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    CopyPreservedArchivePath(preservedStagingPath, stagingPath, archivePath);
                }
            }

            await WriteManifestAsync(stagingPath, manifest, cancellationToken);
            WriteContentIndex(stagingPath);
            WriteArchive(stagingPath, stackOutputPath);
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
            TryDeleteDirectory(preservedStagingPath);
        }
    }

    public static async Task RewriteManifestAsync(
        string sourceStackPath,
        SunderStackManifest manifest,
        string stackOutputPath,
        CancellationToken cancellationToken = default)
        => await MergeAsync(sourceStackPath, null, manifest, stackOutputPath, [], cancellationToken);

    private static async Task BuildStackLayoutAsync(
        string stagingPath,
        SunderStackManifest manifest,
        IReadOnlyDictionary<string, string> payloadFiles,
        CancellationToken cancellationToken)
    {
        await WriteManifestAsync(stagingPath, manifest, cancellationToken);

        foreach (var (relativePath, sourcePath) in payloadFiles)
        {
            ValidateArchiveRelativePath(relativePath);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Stack payload source file '{sourcePath}' was not found.", sourcePath);
            }

            var normalizedPath = relativePath.Replace('\\', '/');
            if (!normalizedPath.StartsWith(SunderStackFormat.PayloadRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Stack payload file '{relativePath}' must be written under payload/.");
            }

            var destinationPath = Path.Combine(stagingPath, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    private static async Task WriteManifestAsync(
        string stagingPath,
        SunderStackManifest manifest,
        CancellationToken cancellationToken)
    {
        var manifestOutputPath = Path.Combine(stagingPath, SunderStackFormat.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(manifestOutputPath)!);
        await File.WriteAllTextAsync(manifestOutputPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine, cancellationToken);
    }

    private static void WriteArchive(string stagingPath, string stackOutputPath)
    {
        var outputDirectory = Path.GetDirectoryName(stackOutputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        if (File.Exists(stackOutputPath))
        {
            File.Delete(stackOutputPath);
        }

        ZipFile.CreateFromDirectory(stagingPath, stackOutputPath, CompressionLevel.Optimal, includeBaseDirectory: false);
    }

    private static void CopyPreservedArchivePath(string sourceRoot, string destinationRoot, string archivePath)
    {
        ValidateArchiveRelativePath(archivePath);
        var normalizedPath = archivePath.Replace('\\', '/').TrimStart('/');
        var sourcePath = Path.Combine(sourceRoot, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
        var destinationPath = Path.Combine(destinationRoot, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(sourcePath))
        {
            CopyDirectory(sourcePath, destinationPath);
            return;
        }

        if (!File.Exists(sourcePath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var destinationFile = Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, overwrite: true);
        }
    }

    private static void WriteContentIndex(string stagingPath)
    {
        var files = Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories)
            .Where(static path => !string.Equals(Path.GetFileName(path), "content-index.json", StringComparison.OrdinalIgnoreCase))
            .Select(path => CreateIndexEntry(stagingPath, path))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var index = new SunderStackContentIndex(SunderStackFormat.CurrentContentIndexVersion, files);
        var outputPath = Path.Combine(stagingPath, SunderStackFormat.ContentIndexPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(index, JsonOptions) + Environment.NewLine);
    }

    private static SunderStackContentIndexEntry CreateIndexEntry(string stagingPath, string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        var relativePath = Path.GetRelativePath(stagingPath, filePath).Replace('\\', '/');
        return new SunderStackContentIndexEntry(
            relativePath,
            Convert.ToHexString(hash).ToLowerInvariant(),
            stream.Length);
    }

    private static void ValidateArchiveRelativePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            throw new InvalidOperationException($"Stack payload path '{path}' must be relative.");
        }

        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static segment => segment == ".."))
        {
            throw new InvalidOperationException($"Stack payload path '{path}' must not contain parent directory traversal.");
        }
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
            // Best effort cleanup for temporary Stack staging.
        }
    }
}
