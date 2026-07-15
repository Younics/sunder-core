using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sunder.Package.Format;

public static class SunderStackArchiveWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private static readonly SunderPackageJsonContext JsonContext = new(JsonOptions);

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
            await SunderArchive.WriteDeterministicAsync(stagingPath, stackOutputPath, cancellationToken);
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
                             .Distinct(StringComparer.Ordinal))
                {
                    CopyPreservedArchivePath(preservedStagingPath, stagingPath, archivePath);
                }
            }

            await WriteManifestAsync(stagingPath, manifest, cancellationToken);
            WriteContentIndex(stagingPath);
            await SunderArchive.WriteDeterministicAsync(stagingPath, stackOutputPath, cancellationToken);
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

        var portablePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relativePath, sourcePath) in payloadFiles.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            var archivePath = ValidateArchiveRelativePath(relativePath);
            if (portablePaths.TryGetValue(archivePath.ToString(), out var existing))
            {
                throw new InvalidOperationException($"Stack payload paths '{existing}' and '{relativePath}' collide on portable filesystems.");
            }

            portablePaths.Add(archivePath.ToString(), relativePath);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Stack payload source file '{sourcePath}' was not found.", sourcePath);
            }

            if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Stack payload source file '{sourcePath}' is a symbolic link or reparse point.");
            }

            var normalizedPath = archivePath.ToString();
            if (!normalizedPath.StartsWith(SunderStackFormat.PayloadRoot, StringComparison.Ordinal)
                || !SunderStackFormat.IsAllowedArchivePath(normalizedPath))
            {
                throw new InvalidOperationException(
                    $"Stack payload file '{relativePath}' must be written under payload/fragments/, payload/files/, or payload/media/.");
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
        await File.WriteAllTextAsync(manifestOutputPath, JsonSerializer.Serialize(manifest, JsonContext.SunderStackManifest) + "\n", cancellationToken);
    }

    private static void CopyPreservedArchivePath(string sourceRoot, string destinationRoot, string archivePath)
    {
        var normalizedPath = ValidateArchiveRelativePath(archivePath).ToString();
        if (!normalizedPath.StartsWith(SunderStackFormat.PayloadRoot, StringComparison.Ordinal)
            || !SunderStackFormat.IsAllowedArchivePath(normalizedPath))
        {
            throw new InvalidOperationException($"Preserved Stack path '{archivePath}' must be under a canonical payload root.");
        }
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
        if ((File.GetAttributes(sourceDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Stack preserved directory '{sourceDirectory}' is a symbolic link or reparse point.");
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Stack preserved directory '{directory}' is a symbolic link or reparse point.");
            }

            Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Stack preserved file '{file}' is a symbolic link or reparse point.");
            }

            var destinationFile = Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, overwrite: true);
        }
    }

    private static void WriteContentIndex(string stagingPath)
    {
        var files = Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories)
            .Where(path => !SunderStackFormat.IsContentIndexPath(ToArchivePath(stagingPath, path)))
            .Select(path => CreateIndexEntry(stagingPath, path))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();

        var index = new SunderStackContentIndex(SunderStackFormat.CurrentContentIndexVersion, files);
        var outputPath = Path.Combine(stagingPath, SunderStackFormat.ContentIndexPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(index, JsonContext.SunderStackContentIndex) + "\n");
    }

    private static SunderStackContentIndexEntry CreateIndexEntry(string stagingPath, string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        var relativePath = Path.GetRelativePath(stagingPath, filePath).Replace('\\', '/');
        if (!SunderStackFormat.IsAllowedArchivePath(relativePath))
        {
            throw new InvalidDataException($"Stack staging contains file outside canonical archive roots: '{relativePath}'.");
        }
        return new SunderStackContentIndexEntry(
            relativePath,
            Convert.ToHexString(hash).ToLowerInvariant(),
            stream.Length);
    }

    private static string ToArchivePath(string stagingPath, string filePath)
        => Path.GetRelativePath(stagingPath, filePath).Replace('\\', '/');

    private static ArchiveRelativePath ValidateArchiveRelativePath(string path)
    {
        try
        {
            return ArchiveRelativePath.Parse(path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException($"Stack payload path '{path}' is unsafe.", ex);
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
