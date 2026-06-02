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
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
    }

    private static async Task BuildStackLayoutAsync(
        string stagingPath,
        SunderStackManifest manifest,
        IReadOnlyDictionary<string, string> payloadFiles,
        CancellationToken cancellationToken)
    {
        var manifestOutputPath = Path.Combine(stagingPath, "manifest", "sunder-stack.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestOutputPath)!);
        await File.WriteAllTextAsync(manifestOutputPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine, cancellationToken);

        foreach (var (relativePath, sourcePath) in payloadFiles)
        {
            ValidateArchiveRelativePath(relativePath);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Stack payload source file '{sourcePath}' was not found.", sourcePath);
            }

            var normalizedPath = relativePath.Replace('\\', '/');
            if (!normalizedPath.StartsWith("payload/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Stack payload file '{relativePath}' must be written under payload/.");
            }

            var destinationPath = Path.Combine(stagingPath, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    private static void WriteContentIndex(string stagingPath)
    {
        var files = Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories)
            .Where(static path => !string.Equals(Path.GetFileName(path), "content-index.json", StringComparison.OrdinalIgnoreCase))
            .Select(path => CreateIndexEntry(stagingPath, path))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var index = new SunderStackContentIndex(1, files);
        var outputPath = Path.Combine(stagingPath, "manifest", "content-index.json");
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
            stream.Length,
            ResolveRole(relativePath));
    }

    private static string ResolveRole(string relativePath)
    {
        if (relativePath.StartsWith("payload/fragments/", StringComparison.OrdinalIgnoreCase))
        {
            return "stack-fragment";
        }

        if (relativePath.StartsWith("payload/files/", StringComparison.OrdinalIgnoreCase))
        {
            return "stack-file";
        }

        if (relativePath.StartsWith("manifest/", StringComparison.OrdinalIgnoreCase))
        {
            return "manifest";
        }

        return "file";
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
