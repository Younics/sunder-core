using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Tasks;

public sealed class EmitSunderDevOutputTask : Microsoft.Build.Utilities.Task
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [Required]
    public string ManifestPath { get; set; } = string.Empty;

    [Required]
    public string DevPackagePath { get; set; } = string.Empty;

    [Required]
    public string DiscoveryRoot { get; set; } = string.Empty;

    [Required]
    public string TargetDirectory { get; set; } = string.Empty;

    public string? AssetsDirectory { get; set; }

    public ITaskItem[] ManagedFiles { get; set; } = [];

    public ITaskItem[] NativeRuntimeFiles { get; set; } = [];

    public ITaskItem[] AssetFiles { get; set; } = [];

    public ITaskItem[] ContractFiles { get; set; } = [];

    public override bool Execute()
    {
        string? stagingPath = null;
        string? stagingMarkerPath = null;
        try
        {
            if (!File.Exists(ManifestPath))
            {
                Log.LogError($"Generated Sunder package manifest '{ManifestPath}' does not exist.");
                return false;
            }

            var outputPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DevPackagePath));
            var markerPath = ValidateSunderDevOutputPathTask.GetOwnershipMarkerPath(outputPath);
            var discoveryKey = GeneratedOutputLock.TargetLeafDiscoveryKey(DiscoveryRoot, outputPath);
            using var outputLock = GeneratedOutputLock.Acquire([discoveryKey], [outputPath]);
            GeneratedOutputTransaction.RecoverAndCleanup(outputPath, markerPath);
            if (File.Exists(outputPath)
                || Directory.Exists(outputPath)
                && (GeneratedOutputPathSafety.IsReparsePoint(outputPath)
                    || !File.Exists(markerPath)
                    || GeneratedOutputPathSafety.IsReparsePoint(markerPath)))
            {
                Log.LogError(
                    $"Sunder dev output '{DevPackagePath}' is not a safely marked generated directory and will not be replaced.");
                return false;
            }

            stagingPath = outputPath + ".stage-" + Guid.NewGuid().ToString("N");
            stagingMarkerPath = markerPath + ".stage-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stagingPath);
            var manifest = JsonSerializer.Deserialize<SunderPackageManifest>(File.ReadAllText(ManifestPath), JsonOptions)
                           ?? throw new InvalidDataException("Generated Sunder package manifest is empty.");
            var targetKeys = SunderPackageTargetResolver.EnumerateTargets(manifest);
            var destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            Copy(
                ManifestPath,
                SunderPackageFormat.ManifestPath,
                destinations,
                stagingPath);
            foreach (var item in ManagedFiles)
            {
                var relative = GetRelativePath(TargetDirectory, item.ItemSpec, "managed output");
                if (relative.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith(ValidateSunderDevOutputPathTask.GeneratedDirectoryName + "/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                Copy(item.ItemSpec, SunderPackageFormat.SharedPayloadRoot + "lib/" + relative, destinations, stagingPath);
            }

            var assetRoot = string.IsNullOrWhiteSpace(AssetsDirectory)
                ? Path.Combine(Directory.GetCurrentDirectory(), "Assets")
                : AssetsDirectory;
            foreach (var item in AssetFiles)
            {
                var relative = GetRelativePath(assetRoot, item.ItemSpec, "asset");
                Copy(item.ItemSpec, SunderPackageFormat.SharedPayloadRoot + "assets/" + relative, destinations, stagingPath);
            }

            foreach (var item in ContractFiles)
            {
                var descriptorPath = item.GetMetadata("DescriptorPath");
                if (string.IsNullOrWhiteSpace(descriptorPath))
                {
                    throw new InvalidDataException($"Sunder contract file '{item.ItemSpec}' is missing DescriptorPath metadata.");
                }
                Copy(item.ItemSpec, SunderPackageFormat.SharedPayloadRoot + descriptorPath, destinations, stagingPath);
            }

            foreach (var item in NativeRuntimeFiles)
            {
                var relative = GetRelativePath(TargetDirectory, item.ItemSpec, "native runtime output");
                var segments = relative.Split('/');
                if (segments.Length < 3
                    || !string.Equals(segments[0], "runtimes", StringComparison.Ordinal)
                    || !SunderPackageFormat.IsRuntimeIdentifier(segments[1]))
                {
                    continue;
                }

                foreach (var target in targetKeys.Where(target => target.Rid == segments[1]))
                {
                    Copy(
                        item.ItemSpec,
                        $"payload/{target.Role}/{target.Rid}/lib/{relative}",
                        destinations,
                        stagingPath);
                }
            }

            PackageContentIndexer.Write(stagingPath);
            var validation = SunderPackageArchiveInspector
                .ValidateExtractedPackageAsync(stagingPath)
                .GetAwaiter()
                .GetResult();
            foreach (var warning in validation.Warnings)
            {
                Log.LogWarning(warning);
            }
            foreach (var error in validation.Errors)
            {
                Log.LogError(error);
            }
            if (!validation.Success)
            {
                return false;
            }

            File.WriteAllText(stagingMarkerPath, "Sunder.Package.Build generated output v1\n");
            GeneratedOutputTransaction.Commit(
                new GeneratedOutputTransaction.StagedOutput(outputPath, stagingPath),
                new GeneratedOutputTransaction.StagedOutput(markerPath, stagingMarkerPath));
            stagingPath = null;
            stagingMarkerPath = null;
            Log.LogMessage(MessageImportance.High, $"Emitted canonical Sunder dev package to {outputPath}");
            return true;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: false);
            return false;
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
            TryDeleteFile(stagingMarkerPath);
        }
    }

    private void Copy(
        string sourcePath,
        string archivePath,
        IDictionary<string, string> destinations,
        string outputPath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Sunder package input file '{sourcePath}' does not exist.", sourcePath);
        }
        if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Sunder package input file '{sourcePath}' must not be a symbolic link or reparse point.");
        }

        var normalizedPath = ArchiveRelativePath.Parse(
            archivePath,
            SunderPackageFormat.MaxArchivePathLength,
            SunderPackageFormat.MaxArchivePathDepth);
        if (!SunderPackageFormat.IsAllowedArchivePath(normalizedPath.ToString()))
        {
            throw new InvalidDataException($"Sunder package output path '{normalizedPath}' is outside canonical roots.");
        }
        if (destinations.TryGetValue(normalizedPath.ToString(), out var existingSource))
        {
            if (string.Equals(Path.GetFullPath(existingSource), Path.GetFullPath(sourcePath), PathComparison))
            {
                return;
            }
            throw new InvalidDataException(
                $"Sunder package inputs '{existingSource}' and '{sourcePath}' collide at '{normalizedPath}'.");
        }
        destinations.Add(normalizedPath.ToString(), sourcePath);

        var destinationPath = normalizedPath.ToPlatformPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: false);
    }

    private static string GetRelativePath(string rootPath, string filePath, string label)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var fullPath = Path.GetFullPath(filePath);
        var relative = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException($"Sunder package {label} '{filePath}' is outside '{rootPath}'.");
        }
        return ArchiveRelativePath.Parse(
            relative,
            SunderPackageFormat.MaxLogicalPathLength,
            SunderPackageFormat.MaxLogicalPathDepth).ToString();
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (path is not null && Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Preserve the primary emit result or failure.
        }
    }

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Preserve the primary emit result or failure.
        }
    }
}
