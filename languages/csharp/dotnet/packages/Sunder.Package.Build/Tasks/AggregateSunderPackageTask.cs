using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Tasks;

/// <summary>Aggregates validated exact-target Manifest V3 leaves into one canonical package tree.</summary>
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

    [Required]
    public string OutputDiscoveryRoot { get; set; } = string.Empty;

    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    [Required]
    public string TargetDirectory { get; set; } = string.Empty;

    [Required]
    public string ExpectedPackageVersion { get; set; } = string.Empty;

    [Output]
    public string NormalizedOutputPath { get; private set; } = string.Empty;

    public override bool Execute()
    {
        string? temporaryPath = null;
        string? temporaryMarkerPath = null;
        try
        {
            if (TargetLeaves.Length == 0)
            {
                Log.LogError("AggregateSunderPackageTask requires at least one SunderPackageTargetLeaf.");
                return false;
            }

            if (!GeneratedOutputPathSafety.TryValidateDirectory(
                    OutputPath,
                    ProjectDirectory,
                    TargetDirectory,
                    ValidateSunderDevOutputPathTask.GeneratedDirectoryName,
                    out var output,
                    out var pathError))
            {
                Log.LogError($"Aggregate output '{OutputPath}' is unsafe. {pathError}");
                return false;
            }

            var markerPath = output + MarkerFileSuffix;
            var leafInputs = TargetLeaves
                .Select(item =>
                {
                    var leafRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.ItemSpec));
                    var discoveryRoot = item.GetMetadata("DiscoveryRoot");
                    if (string.IsNullOrWhiteSpace(discoveryRoot))
                    {
                        throw new InvalidDataException($"SunderPackageTargetLeaf '{item.ItemSpec}' requires explicit DiscoveryRoot metadata.");
                    }
                    return (LeafRoot: leafRoot, DiscoveryRoot: discoveryRoot);
                })
                .ToArray();
            var leafRoots = leafInputs.Select(static input => input.LeafRoot).ToArray();
            var discoveryKeys = leafInputs
                .Select(input => GeneratedOutputLock.TargetLeafDiscoveryKey(input.DiscoveryRoot, input.LeafRoot))
                .Append(GeneratedOutputLock.TargetLeafDiscoveryKey(OutputDiscoveryRoot, output));
            using var outputLock = GeneratedOutputLock.Acquire(discoveryKeys, leafRoots.Append(output));
            GeneratedOutputTransaction.RecoverAndCleanup(output, markerPath);
            foreach (var leafRoot in leafRoots)
            {
                GeneratedOutputTransaction.RecoverAndCleanup(
                    leafRoot,
                    ValidateSunderDevOutputPathTask.GetOwnershipMarkerPath(leafRoot),
                    leafRoot + MarkerFileSuffix);
            }
            if (File.Exists(output))
            {
                Log.LogError($"Aggregate output '{OutputPath}' is a file and will not be replaced.");
                return false;
            }
            if (Directory.Exists(output))
            {
                if (!File.Exists(markerPath)
                    || GeneratedOutputPathSafety.IsReparsePoint(output)
                    || GeneratedOutputPathSafety.IsReparsePoint(markerPath))
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
            var roleContracts = MergeRoleScopedContracts(leaves);
            if (roleContracts is null)
            {
                return false;
            }
            var stampedTargetManifests = PackageTargetBuilder.StampRpcV2Capabilities(
                targets.Select(static target => target.Manifest).ToArray(),
                roleContracts.RpcRoles,
                Log);
            if (stampedTargetManifests is null)
            {
                return false;
            }
            var stampedTargets = stampedTargetManifests.ToDictionary(
                static target => new SunderPackageTargetKey(target.Role!, target.Rid!));
            targets = targets.Select(target => target with { Manifest = stampedTargets[target.Key] }).ToArray();

            var parent = Path.GetDirectoryName(output)
                         ?? throw new InvalidOperationException($"Aggregate output '{output}' has no parent directory.");
            Directory.CreateDirectory(parent);
            temporaryPath = output + ".stage-" + Guid.NewGuid().ToString("N");
            temporaryMarkerPath = markerPath + ".stage-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(temporaryPath);
            WriteAggregate(
                temporaryPath,
                leaves[0].Manifest,
                targets,
                roleContracts.Uses,
                roleContracts.Providers);

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

            File.WriteAllText(temporaryMarkerPath, "Sunder.Package.Build aggregate generated output v1\n");
            GeneratedOutputTransaction.Commit(
                new GeneratedOutputTransaction.StagedOutput(output, temporaryPath),
                new GeneratedOutputTransaction.StagedOutput(markerPath, temporaryMarkerPath));
            temporaryPath = null;
            temporaryMarkerPath = null;
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
            TryDeleteFile(temporaryMarkerPath);
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
            if (!string.Equals(manifest.Version, ExpectedPackageVersion, StringComparison.Ordinal))
            {
                Log.LogError(
                    $"Target leaf '{item.ItemSpec}' package version '{manifest.Version}' does not match "
                    + $"aggregate expected version '{ExpectedPackageVersion}'.");
                continue;
            }
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
        => JsonSerializer.SerializeToUtf8Bytes(CloneManifest(source, [], [], []), JsonOptions);

    private RoleScopedContracts? MergeRoleScopedContracts(IReadOnlyList<Leaf> leaves)
    {
        var uses = new Dictionary<string, (SunderPackageContractUseManifest Value, string Leaf)>(StringComparer.Ordinal);
        var providers = new Dictionary<string, (SunderPackageProviderManifest Value, string Leaf)>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            foreach (var use in (leaf.Manifest.UsesContracts ?? []).OfType<SunderPackageContractUseManifest>())
            {
                var key = RoleKey(use.Role!, use.ContractId!);
                if (uses.TryGetValue(key, out var existing))
                {
                    if (!UsesAreIdentical(existing.Value, use))
                    {
                        Log.LogError(
                            $"Target leaves '{existing.Leaf}' and '{leaf.RootPath}' declare conflicting contract use '{use.ContractId}' for role '{use.Role}'.");
                    }
                }
                else
                {
                    uses.Add(key, (use, leaf.RootPath));
                }
            }

            foreach (var provider in (leaf.Manifest.Provides ?? []).OfType<SunderPackageProviderManifest>())
            {
                var key = RoleKey(provider.Role!, provider.ProviderId!);
                if (providers.TryGetValue(key, out var existing))
                {
                    if (!ProvidersAreIdentical(existing.Value, provider))
                    {
                        Log.LogError(
                            $"Target leaves '{existing.Leaf}' and '{leaf.RootPath}' declare conflicting provider '{provider.ProviderId}' for role '{provider.Role}'.");
                    }
                }
                else
                {
                    providers.Add(key, (provider, leaf.RootPath));
                }
            }
        }

        ValidateRoleScopedDeclarationSets(leaves);
        if (Log.HasLoggedErrors) return null;
        var mergedUses = uses.Values.Select(static item => item.Value)
            .OrderBy(static use => RoleOrder(use.Role))
            .ThenBy(static use => use.ContractId, StringComparer.Ordinal)
            .Cast<SunderPackageContractUseManifest?>()
            .ToArray();
        var mergedProviders = providers.Values.Select(static item => item.Value)
            .OrderBy(static provider => RoleOrder(provider.Role))
            .ThenBy(static provider => provider.ProviderId, StringComparer.Ordinal)
            .Cast<SunderPackageProviderManifest?>()
            .ToArray();
        var rpcRoles = mergedUses.Select(static use => use!.Role!)
            .Concat(mergedProviders.Select(static provider => provider!.Role!))
            .ToHashSet(StringComparer.Ordinal);
        return new RoleScopedContracts(mergedUses, mergedProviders, rpcRoles);
    }

    private void ValidateRoleScopedDeclarationSets(IReadOnlyList<Leaf> leaves)
    {
        var roles = leaves.SelectMany(static leaf => leaf.Targets)
            .Select(static target => target.Key.Role)
            .Distinct(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            var roleLeaves = leaves
                .Where(leaf => leaf.Targets.Any(target => target.Key.Role == role))
                .ToArray();
            if (roleLeaves.Length < 2) continue;

            var expectedUses = UseKeys(roleLeaves[0], role);
            var expectedProviders = ProviderKeys(roleLeaves[0], role);
            foreach (var leaf in roleLeaves.Skip(1))
            {
                if (!expectedUses.SetEquals(UseKeys(leaf, role)))
                {
                    Log.LogError(
                        $"Target leaves '{roleLeaves[0].RootPath}' and '{leaf.RootPath}' must declare identical contract use sets for role '{role}'; declarations cannot be omitted from individual RID leaves.");
                }
                if (!expectedProviders.SetEquals(ProviderKeys(leaf, role)))
                {
                    Log.LogError(
                        $"Target leaves '{roleLeaves[0].RootPath}' and '{leaf.RootPath}' must declare identical provider sets for role '{role}'; declarations cannot be omitted from individual RID leaves.");
                }
            }
        }
    }

    private static HashSet<string> UseKeys(Leaf leaf, string role)
        => (leaf.Manifest.UsesContracts ?? [])
            .OfType<SunderPackageContractUseManifest>()
            .Where(use => use.Role == role)
            .Select(static use => use.ContractId!)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> ProviderKeys(Leaf leaf, string role)
        => (leaf.Manifest.Provides ?? [])
            .OfType<SunderPackageProviderManifest>()
            .Where(provider => provider.Role == role)
            .Select(static provider => provider.ProviderId!)
            .ToHashSet(StringComparer.Ordinal);

    private static void WriteAggregate(
        string outputPath,
        SunderPackageManifest sourceManifest,
        IReadOnlyList<TargetLeaf> targets,
        IReadOnlyList<SunderPackageContractUseManifest?> uses,
        IReadOnlyList<SunderPackageProviderManifest?> providers)
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
            if (occurrences.Any(target =>
                    target.Manifest.Kind == SunderPackageFormat.WorkerTargetKind
                    && string.Equals(target.Manifest.EntryPoint, logicalPath, StringComparison.Ordinal)))
            {
                foreach (var target in occurrences)
                {
                    Copy(
                        target.Files[logicalPath],
                        $"payload/{target.Key.Role}/{target.Key.Rid}/{logicalPath}",
                        outputPath,
                        destinations);
                }
                continue;
            }
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

        var manifest = CloneManifest(
            sourceManifest,
            orderedTargets.Select(static target => target.Manifest).ToArray(),
            uses,
            providers);
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
        IReadOnlyList<SunderPackageTargetManifest?> targets,
        IReadOnlyList<SunderPackageContractUseManifest?> uses,
        IReadOnlyList<SunderPackageProviderManifest?> providers)
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
            UsesContracts = uses,
            Provides = providers,
        };

    private static bool UsesAreIdentical(
        SunderPackageContractUseManifest left,
        SunderPackageContractUseManifest right)
        => left.Role == right.Role
           && left.ContractId == right.ContractId
           && left.ContractVersion == right.ContractVersion
           && left.AbiSha256 == right.AbiSha256
           && left.Required == right.Required
           && (left.Actions ?? []).SequenceEqual(right.Actions ?? [], StringComparer.Ordinal);

    private static bool ProvidersAreIdentical(
        SunderPackageProviderManifest left,
        SunderPackageProviderManifest right)
        => left.ProviderId == right.ProviderId
           && left.Role == right.Role
           && left.ContractId == right.ContractId
           && left.ContractVersion == right.ContractVersion
           && left.DescriptorSha256 == right.DescriptorSha256
           && left.AbiSha256 == right.AbiSha256
           && left.Audience == right.Audience;

    private static string RoleKey(string role, string id) => role + "\0" + id;

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

    private static int RoleOrder(string? role)
        => role == SunderPackageFormat.AppHostRole ? 0 : 1;

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

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path)) File.Delete(path);
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

    private sealed record RoleScopedContracts(
        IReadOnlyList<SunderPackageContractUseManifest?> Uses,
        IReadOnlyList<SunderPackageProviderManifest?> Providers,
        IReadOnlySet<string> RpcRoles);
}
