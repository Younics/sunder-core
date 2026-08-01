using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Tasks;

public sealed class AggregateSunderPackageTask : Microsoft.Build.Utilities.Task
{
    public const string MarkerFileSuffix = ".sunder-aggregate-generated-output";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Required]
    public ITaskItem[] TargetLeaves { get; set; } = [];

    [Required]
    public string OutputPath { get; set; } = string.Empty;

    [Output]
    public string NormalizedOutputPath { get; private set; } = string.Empty;

    public override bool Execute()
    {
        string? temporaryPath = null;
        try
        {
            if (TargetLeaves.Length == 0)
            {
                Log.LogError("AggregateSunderPackageTask requires at least one SunderPackageTargetLeaf.");
                return false;
            }

            var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(OutputPath));
            var markerPath = output + MarkerFileSuffix;
            if (Directory.Exists(output))
            {
                if (!File.Exists(markerPath)
                    || (File.GetAttributes(output) & FileAttributes.ReparsePoint) != 0
                    || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
                {
                    Log.LogError(
                        $"Aggregate output '{OutputPath}' already exists without its safe generated-output marker and will not be replaced.");
                    return false;
                }
            }

            var leaves = ReadLeaves();
            if (leaves is null)
            {
                return false;
            }
            var targets = leaves.SelectMany(static leaf => leaf.Targets).ToArray();
            var duplicate = targets.GroupBy(static target => target.Key).FirstOrDefault(static group => group.Count() > 1);
            if (duplicate is not null)
            {
                Log.LogError($"Aggregate inputs declare exact target '{duplicate.Key}' more than once.");
                return false;
            }

            VerifyMetadataAgreement(leaves);
            if (Log.HasLoggedErrors)
            {
                return false;
            }

            var parent = Path.GetDirectoryName(output)
                         ?? throw new InvalidOperationException($"Aggregate output '{output}' has no parent directory.");
            Directory.CreateDirectory(parent);
            temporaryPath = Path.Combine(parent, $".{Path.GetFileName(output)}.aggregate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryPath);
            WriteAggregate(temporaryPath, leaves[0].Manifest, targets);

            PackageContentIndexer.Write(temporaryPath);
            var validation = SunderPackageArchiveInspector
                .ValidateExtractedPackageAsync(temporaryPath)
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

            Publish(temporaryPath, output);
            temporaryPath = null;
            File.WriteAllText(markerPath, "Sunder.Package.Build aggregate generated output v1\n");
            NormalizedOutputPath = output + Path.DirectorySeparatorChar;
            Log.LogMessage(MessageImportance.High, $"Aggregated canonical Sunder package at {output}");
            return true;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: false);
            return false;
        }
        finally
        {
            TryDeleteDirectory(temporaryPath);
        }
    }

    private IReadOnlyList<Leaf>? ReadLeaves()
    {
        var leaves = new List<Leaf>(TargetLeaves.Length);
        foreach (var item in TargetLeaves)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.ItemSpec));
            if (!Directory.Exists(root))
            {
                Log.LogError($"Sunder package target leaf '{item.ItemSpec}' does not exist.");
                continue;
            }
            var validation = SunderPackageArchiveInspector
                .ValidateExtractedPackageAsync(root)
                .GetAwaiter()
                .GetResult();
            if (!validation.Success)
            {
                foreach (var error in validation.Errors)
                {
                    Log.LogError($"Target leaf '{item.ItemSpec}': {error}");
                }
                continue;
            }

            var manifest = validation.Manifest!;
            var contentIndex = validation.ContentIndex!;
            var indexEntries = contentIndex.Files!
                .Where(static entry => entry?.Path is not null)
                .ToDictionary(static entry => entry!.Path!, StringComparer.Ordinal);
            var targets = new List<TargetLeaf>();
            foreach (var key in SunderPackageTargetResolver.EnumerateTargets(manifest))
            {
                SunderPackageTargetResolver.TryResolveTarget(manifest, key, out var target);
                var files = new Dictionary<string, LeafFile>(StringComparer.Ordinal);
                foreach (var projection in SunderPackageTargetResolver.CreateProjectionPlan(manifest, contentIndex, key).Files)
                {
                    var physical = projection.PhysicalPath.ToString();
                    var entry = indexEntries[physical];
                    files.Add(
                        projection.LogicalPath.ToString(),
                        new LeafFile(
                            projection.PhysicalPath.ToPlatformPath(root),
                            entry.Sha256!,
                            entry.Size));
                }
                targets.Add(new TargetLeaf(key, target!, files));
            }

            ValidateDescriptor(item, targets);
            leaves.Add(new Leaf(root, manifest, targets));
        }
        return Log.HasLoggedErrors ? null : leaves;
    }

    private void ValidateDescriptor(ITaskItem item, IReadOnlyList<TargetLeaf> targets)
    {
        var role = OptionalMetadata(item, "Role");
        var rid = OptionalMetadata(item, "Rid");
        var kind = OptionalMetadata(item, "Kind");
        var entryPoint = OptionalMetadata(item, "EntryPoint");
        var viewsJson = OptionalMetadata(item, "ViewsJson");
        if (role is null && rid is null && kind is null && entryPoint is null && viewsJson is null)
        {
            return;
        }
        if (targets.Count != 1)
        {
            Log.LogError(
                $"Target leaf '{item.ItemSpec}' supplies descriptor metadata but contains {targets.Count} exact targets; descriptor metadata requires one target.");
            return;
        }

        var target = targets[0];
        if (role is not null && role != target.Key.Role
            || rid is not null && rid != target.Key.Rid
            || kind is not null && kind != target.Manifest.Kind
            || entryPoint is not null && PackageAssetDiscovery.NormalizePath(entryPoint) != target.Manifest.EntryPoint
            || viewsJson is not null
            && !JsonSerializer.Serialize(target.Manifest.Views ?? []).Equals(viewsJson, StringComparison.Ordinal))
        {
            Log.LogError($"Target leaf '{item.ItemSpec}' descriptor metadata does not match its manifest target.");
        }
    }

    private void VerifyMetadataAgreement(IReadOnlyList<Leaf> leaves)
    {
        var expected = MetadataFingerprint(leaves[0].Manifest);
        foreach (var leaf in leaves.Skip(1))
        {
            if (!expected.AsSpan().SequenceEqual(MetadataFingerprint(leaf.Manifest)))
            {
                Log.LogError(
                    $"Target leaf '{leaf.RootPath}' package-wide manifest metadata does not agree with '{leaves[0].RootPath}'.");
            }
        }
    }

    private static byte[] MetadataFingerprint(SunderPackageManifest source)
        => JsonSerializer.SerializeToUtf8Bytes(CloneManifest(source, []), JsonOptions);

    private static void WriteAggregate(
        string outputPath,
        SunderPackageManifest sourceManifest,
        IReadOnlyList<TargetLeaf> targets)
    {
        var orderedTargets = targets
            .OrderBy(static target => target.Key.Role == SunderPackageFormat.AppHostRole ? 0 : 1)
            .ThenBy(static target => RidOrder(target.Key.Rid))
            .ToArray();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logicalPaths = orderedTargets
            .SelectMany(static target => target.Files.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (var logicalPath in logicalPaths)
        {
            var occurrences = orderedTargets
                .Where(target => target.Files.ContainsKey(logicalPath))
                .ToArray();
            if (occurrences.Length == orderedTargets.Length && HaveIdenticalFile(occurrences, logicalPath))
            {
                Copy(occurrences[0].Files[logicalPath], SunderPackageFormat.SharedPayloadRoot + logicalPath, outputPath, destinations);
                continue;
            }

            foreach (var roleGroup in orderedTargets.GroupBy(static target => target.Key.Role))
            {
                var roleTargets = roleGroup.ToArray();
                var roleOccurrences = roleTargets.Where(target => target.Files.ContainsKey(logicalPath)).ToArray();
                if (roleOccurrences.Length == roleTargets.Length && HaveIdenticalFile(roleOccurrences, logicalPath))
                {
                    Copy(
                        roleOccurrences[0].Files[logicalPath],
                        $"payload/{roleGroup.Key}/shared/{logicalPath}",
                        outputPath,
                        destinations);
                    continue;
                }

                foreach (var target in roleOccurrences)
                {
                    Copy(
                        target.Files[logicalPath],
                        $"payload/{target.Key.Role}/{target.Key.Rid}/{logicalPath}",
                        outputPath,
                        destinations);
                }
            }
        }

        var manifest = CloneManifest(sourceManifest, orderedTargets.Select(static target => target.Manifest).ToArray());
        var manifestPath = ArchiveRelativePath.Parse(SunderPackageFormat.ManifestPath).ToPlatformPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        PackageManifestSerializer.Write(manifestPath, manifest, JsonOptions);
    }

    private static bool HaveIdenticalFile(IReadOnlyList<TargetLeaf> targets, string logicalPath)
    {
        var expected = targets[0].Files[logicalPath];
        return targets.Skip(1).All(target =>
            target.Files[logicalPath].Size == expected.Size
            && string.Equals(target.Files[logicalPath].Sha256, expected.Sha256, StringComparison.Ordinal));
    }

    private static void Copy(
        LeafFile source,
        string archivePath,
        string outputPath,
        ISet<string> destinations)
    {
        var path = ArchiveRelativePath.Parse(
            archivePath,
            SunderPackageFormat.MaxArchivePathLength,
            SunderPackageFormat.MaxArchivePathDepth);
        if (!destinations.Add(path.ToString()))
        {
            throw new InvalidDataException($"Aggregate inputs collide at canonical path '{path}'.");
        }
        var destination = path.ToPlatformPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source.FullPath, destination, overwrite: false);
    }

    private static SunderPackageManifest CloneManifest(
        SunderPackageManifest source,
        IReadOnlyList<SunderPackageTargetManifest?> targets)
        => new()
        {
            ArchiveFormatVersion = source.ArchiveFormatVersion,
            ManifestVersion = source.ManifestVersion,
            Id = source.Id,
            Name = source.Name,
            Summary = source.Summary,
            Version = source.Version,
            Icon = source.Icon,
            DependsOn = source.DependsOn,
            Targets = targets,
            ContractBundles = source.ContractBundles,
            UsesContracts = source.UsesContracts,
            Provides = source.Provides,
        };

    private static void Publish(string temporaryPath, string outputPath)
    {
        string? backupPath = null;
        if (Directory.Exists(outputPath))
        {
            backupPath = outputPath + ".replace-" + Guid.NewGuid().ToString("N");
            Directory.Move(outputPath, backupPath);
        }
        try
        {
            Directory.Move(temporaryPath, outputPath);
            TryDeleteDirectory(backupPath);
        }
        catch
        {
            if (!Directory.Exists(outputPath) && backupPath is not null && Directory.Exists(backupPath))
            {
                Directory.Move(backupPath, outputPath);
            }
            throw;
        }
    }

    private static string? OptionalMetadata(ITaskItem item, string name)
    {
        var value = item.GetMetadata(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int RidOrder(string rid)
    {
        for (var index = 0; index < SunderPackageFormat.SupportedRuntimeIdentifiers.Count; index++)
        {
            if (rid == SunderPackageFormat.SupportedRuntimeIdentifiers[index]) return index;
        }
        return int.MaxValue;
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (path is not null && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Preserve the primary aggregation result or failure.
        }
    }

    private sealed record Leaf(
        string RootPath,
        SunderPackageManifest Manifest,
        IReadOnlyList<TargetLeaf> Targets);

    private sealed record TargetLeaf(
        SunderPackageTargetKey Key,
        SunderPackageTargetManifest Manifest,
        IReadOnlyDictionary<string, LeafFile> Files);

    private sealed record LeafFile(string FullPath, string Sha256, long Size);
}
