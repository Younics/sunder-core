using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Package.Format;
using Sunder.Package.Build.Rpc.Authoring;

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

    public ITaskItem[] WorkerFiles { get; set; } = [];

    public ITaskItem[] AssetFiles { get; set; } = [];

    public ITaskItem[] ContractFiles { get; set; } = [];
    public ITaskItem[] RoleContentFiles { get; set; } = [];
    public string? ContractLockPath { get; set; }
    public string? ProjectDirectory { get; set; }
    public string ContractCacheRoot { get; set; } = SunderRpcContractCache.DefaultRoot;
    public bool AllowDevelopmentRegistryOrigin { get; set; }

    public override bool Execute()
    {
        string? stagingPath = null;
        string? stagingMarkerPath = null;
        string? stagingImportsPath = null;
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
            var importsPath = outputPath + SunderPackageFormat.DevelopmentImportsSuffix;
            stagingImportsPath = importsPath + ".stage-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stagingPath);
            var manifest = StrictJsonSerializer.Deserialize<SunderPackageManifest>(File.ReadAllText(ManifestPath), JsonOptions)
                           ?? throw new InvalidDataException("Generated Sunder package manifest is empty.");
            var targetKeys = SunderPackageTargetResolver.EnumerateTargets(manifest);
            var destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var workerTargets = (manifest.Targets ?? [])
                .Where(static target => target?.Kind == SunderPackageFormat.WorkerTargetKind)
                .Select(static target => target!)
                .ToArray();

            Copy(
                ManifestPath,
                SunderPackageFormat.ManifestPath,
                destinations,
                stagingPath);
            if (workerTargets.Length > 1)
            {
                throw new InvalidDataException("A C# worker leaf may contain exactly one Runtime worker target.");
            }
            if (workerTargets.Length == 1)
            {
                var target = workerTargets[0];
                if (target.EntryPoint is null
                    || !target.EntryPoint.StartsWith("lib/", StringComparison.Ordinal)
                    || target.EntryPoint.Length == "lib/".Length)
                {
                    throw new InvalidDataException(
                        $"C# worker target '{target.Role}/{target.Rid}' must declare its managed module directly under lib/.");
                }
                var expectedEntryPointRelativePath = target.EntryPoint["lib/".Length..];
                var workerFiles = WorkerFiles.Select(item =>
                {
                    var configuredRelative = item.GetMetadata("RelativePath");
                    var relative = string.IsNullOrWhiteSpace(configuredRelative)
                        ? GetRelativePath(TargetDirectory, item.ItemSpec, "worker publish output")
                        : ArchiveRelativePath.Parse(
                            PackageAssetDiscovery.NormalizePath(configuredRelative),
                            SunderPackageFormat.MaxLogicalPathLength,
                            SunderPackageFormat.MaxLogicalPathDepth).ToString();
                    return (Item: item, RelativePath: relative);
                }).ToArray();
                var workerPaths = workerFiles
                    .Select(static file => file.RelativePath)
                    .ToHashSet(StringComparer.Ordinal);
                var entryPointMatches = 0;
                foreach (var file in workerFiles)
                {
                    var item = file.Item;
                    var relative = file.RelativePath;
                    if (DotNetWorkerPackagePolicy.IsSymbolFile(relative)
                        || relative.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                        && workerPaths.Contains(relative[..^".xml".Length] + ".dll"))
                    {
                        continue;
                    }
                    var packageId = item.GetMetadata("NuGetPackageId");
                    if (DotNetWorkerPackagePolicy.IsRuntimePackPackageId(packageId))
                    {
                        throw new InvalidDataException(
                            $"C# worker publish output '{relative}' comes from forbidden runtime pack '{packageId}'.");
                    }
                    if (DotNetWorkerPackagePolicy.IsToolchainPackageId(packageId)
                        || string.Equals(item.GetMetadata("AssetType"), "compile", StringComparison.OrdinalIgnoreCase)
                        || DotNetWorkerPackagePolicy.IsBuildTimePath(relative))
                    {
                        throw new InvalidDataException(
                            $"C# worker publish output contains build-time/toolchain file '{relative}'.");
                    }
                    if (DotNetWorkerPackagePolicy.IsAppHostForEntryPoint(relative, expectedEntryPointRelativePath))
                    {
                        throw new InvalidDataException($"C# worker publish output contains apphost '{relative}'.");
                    }
                    if (DotNetWorkerPackagePolicy.IsForbiddenRuntimeFile(relative))
                    {
                        throw new InvalidDataException(
                            $"C# worker publish output contains forbidden .NET runtime file '{relative}'.");
                    }
                    if (string.Equals(relative, expectedEntryPointRelativePath, StringComparison.Ordinal))
                    {
                        entryPointMatches++;
                    }
                    Copy(
                        file.Item.ItemSpec,
                        $"payload/{SunderPackageFormat.RuntimeHostRole}/{target.Rid}/lib/{relative}",
                        destinations,
                        stagingPath);
                }
                if (entryPointMatches != 1)
                {
                    throw new InvalidDataException(
                        $"C# worker publish output must contain exactly one official managed module '{expectedEntryPointRelativePath}'.");
                }
            }
            else
            {
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
                foreach (var item in RoleContentFiles)
                {
                    var relative = item.GetMetadata("TargetPath");
                    if (relative.Length == 0) relative = item.GetMetadata("Link");
                    relative = ArchiveRelativePath.Parse(PackageAssetDiscovery.NormalizePath(relative), SunderPackageFormat.MaxLogicalPathLength,
                        SunderPackageFormat.MaxLogicalPathDepth).ToString();
                    var source = Sunder.Package.Build.Authoring.SunderPackageResourcePlanner.Copies(item.GetMetadata("CopyToOutputDirectory"))
                        ? Path.Combine(TargetDirectory, relative) : item.ItemSpec;
                    Copy(source, SunderPackageFormat.SharedPayloadRoot + "lib/" + relative, destinations, stagingPath);
                }
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
            if (!destinations.ContainsKey(SunderPackageFormat.RpcModelProjectionPath)
                && !(manifest.ContractBundles ?? []).Any(bundle => bundle?.Exposure == "public"))
            {
                // A producer with no public bundles still emits the required full
                // empty document. Public bundles always require compiler evidence.
                var projection = new SunderRpcModelProjectionDocument(1, manifest.Id!, manifest.Version!, []);
                var path = ArchiveRelativePath.Parse(SunderPackageFormat.RpcModelProjectionPath).ToPlatformPath(stagingPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, projection.ToCanonicalUtf8());
                destinations.Add(SunderPackageFormat.RpcModelProjectionPath, "generated public model projection");
            }

            foreach (var item in workerTargets.Length == 0 ? NativeRuntimeFiles : [])
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
            File.WriteAllBytes(stagingImportsPath, DevelopmentImportHandoff.Create(
                ManifestPath, ContractLockPath, ContractCacheRoot, AllowDevelopmentRegistryOrigin, ProjectDirectory));
            GeneratedOutputTransaction.Commit(
                new GeneratedOutputTransaction.StagedOutput(outputPath, stagingPath),
                new GeneratedOutputTransaction.StagedOutput(markerPath, stagingMarkerPath),
                new GeneratedOutputTransaction.StagedOutput(importsPath, stagingImportsPath));
            stagingPath = null;
            stagingMarkerPath = null;
            stagingImportsPath = null;
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
            TryDeleteFile(stagingImportsPath);
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
