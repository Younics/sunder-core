using System.Reflection.PortableExecutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Tasks;

/// <summary>Aggregates validated exact-target Manifest V1 leaves into one canonical package tree.</summary>
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

    internal bool InputsAlreadyLeased { get; set; }
    internal CancellationToken CancellationToken { get; set; }

    public override bool Execute()
    {
        string? temporaryPath = null;
        string? temporaryMarkerPath = null;
        try
        {
            CancellationToken.ThrowIfCancellationRequested();
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
            using var outputLock = InputsAlreadyLeased
                ? GeneratedOutputLock.Acquire([GeneratedOutputLock.TargetLeafDiscoveryKey(OutputDiscoveryRoot, output)], [output], cancellationToken: CancellationToken)
                : GeneratedOutputLock.Acquire(discoveryKeys, leafRoots.Append(output), cancellationToken: CancellationToken);
            GeneratedOutputTransaction.RecoverAndCleanup(output, markerPath);
            foreach (var leafRoot in InputsAlreadyLeased ? [] : leafRoots)
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

            var packageManifest = VerifyMetadataAgreement(leaves);
            if (packageManifest is null || Log.HasLoggedErrors)
            {
                return false;
            }
            var roleContracts = MergeRoleScopedContracts(leaves);
            if (roleContracts is null)
            {
                return false;
            }
            var stampedTargetManifests = PackageTargetBuilder.StampRpcCapabilities(
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
                packageManifest,
                targets,
                roleContracts.Uses,
                roleContracts.Providers);

            PackageContentIndexer.Write(temporaryPath);
            var validation = SunderPackageArchiveInspector
                .ValidateExtractedPackageAsync(temporaryPath, CancellationToken)
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
            CancellationToken.ThrowIfCancellationRequested();
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
                .ValidateExtractedPackageAsync(root, CancellationToken)
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

    private SunderPackageManifest? VerifyMetadataAgreement(IReadOnlyList<Leaf> leaves)
    {
        try
        {
            // Every RID of one role has the same complete declarations. Different
            // roles may contribute imports; no role may silently truncate an API.
            foreach (var role in leaves.SelectMany(leaf => leaf.Targets).Select(target => target.Key.Role).Distinct(StringComparer.Ordinal))
            {
                var roleLeaves = leaves.Where(leaf => leaf.Targets.Any(target => target.Key.Role == role)).ToArray();
                var expected = MetadataFingerprint(roleLeaves[0].Manifest);
                if (roleLeaves.Skip(1).Any(leaf => !expected.AsSpan().SequenceEqual(MetadataFingerprint(leaf.Manifest))))
                    throw new InvalidDataException($"Target leaves disagree on package-wide manifest metadata/import evidence across RIDs of role '{role}'.");
            }
            var documents = leaves.Select(leaf => JsonSerializer.SerializeToNode(CloneManifest(leaf.Manifest, [], [], []), JsonOptions)!.AsObject()).ToArray();
            string[] groups = ["dependsOn", "contractBundles", "packageArtifactImports", "packageApiImports", "providerImports", "platformImports"];
            JsonObject Identity(JsonObject value)
            {
                var clone = value.DeepClone().AsObject(); foreach (var group in groups) clone.Remove(group); return clone;
            }
            var identity = Identity(documents[0]);
            if (documents.Skip(1).Any(value => !JsonNode.DeepEquals(identity, Identity(value))))
                throw new InvalidDataException("Target leaves disagree on package-wide manifest metadata (identity).");
            var owned = Owned(documents[0]);
            if (documents.Skip(1).Any(value => !JsonNode.DeepEquals(owned, Owned(value))))
                throw new InvalidDataException("Target leaves disagree on package-wide manifest metadata: every role must retain the complete identical owned-contract projection.");
            var merged = documents[0].DeepClone().AsObject();
            merged["contractBundles"] = Merge("contractBundles", value => Text(value, "contractId") + "\0" + Text(value, "version"));
            merged["packageArtifactImports"] = Merge("packageArtifactImports", value => Text(value, "packageId"));
            merged["packageApiImports"] = Merge("packageApiImports", value => Text(value, "packageId"), "useRefs");
            merged["providerImports"] = Merge("providerImports", value => Text(value, "key"), "providerRefs");
            merged["platformImports"] = Merge("platformImports", value => Text(value, "key"), "providerRefs");
            var dependencies = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var item in documents.SelectMany(value => Items(value, "dependsOn")))
            {
                var id = Text(item, "packageId");
                if (!dependencies.TryGetValue(id, out var previous)) dependencies.Add(id, item.DeepClone().AsObject());
                else
                {
                    if (Text(previous, "versionRange") != Text(item, "versionRange")) throw new InvalidDataException($"Dependency '{id}' has conflicting reviewed ranges across roles.");
                    if (Text(item, "presence") == "required") previous["presence"] = "required";
                }
            }
            merged["dependsOn"] = new JsonArray(dependencies.Values.Select(value => (JsonNode)value).ToArray());
            return merged.Deserialize<SunderPackageManifest>(JsonOptions)!;

            JsonArray Merge(string group, Func<JsonObject, string> key, string? roleRefs = null)
            {
                var values = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
                foreach (var item in documents.SelectMany(value => Items(value, group)))
                {
                    var id = key(item);
                    if (!values.TryGetValue(id, out var previous)) { values.Add(id, item.DeepClone().AsObject()); continue; }
                    var left = previous.DeepClone().AsObject(); var right = item.DeepClone().AsObject();
                    if (roleRefs is not null) { left.Remove(roleRefs); right.Remove(roleRefs); }
                    if (!JsonNode.DeepEquals(left, right)) throw new InvalidDataException($"Target leaves have conflicting '{group}' evidence for '{id}'.");
                    if (roleRefs is not null)
                    {
                        var references = Items(previous, roleRefs).Concat(Items(item, roleRefs)).GroupBy(value => value.ToJsonString(), StringComparer.Ordinal)
                            .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => group.First().DeepClone()).ToArray();
                        previous[roleRefs] = new JsonArray(references);
                    }
                }
                return new JsonArray(values.Values.Select(value => (JsonNode)value).ToArray());
            }
            static IEnumerable<JsonObject> Items(JsonObject value, string field) => value[field] is JsonArray values ? values.Select(item => item!.AsObject()) : [];
            static string Text(JsonObject value, string field) => value[field]!.GetValue<string>();
            static JsonArray Owned(JsonObject value) => new(Items(value, "contractBundles").Where(item => Text(item, "source") == "owned")
                .OrderBy(item => Text(item, "contractId"), StringComparer.Ordinal).ThenBy(item => Text(item, "version"), StringComparer.Ordinal).Select(item => item.DeepClone()).ToArray());
        }
        catch (InvalidDataException error) { Log.LogError(error.Message); return null; }
    }

    private static byte[] MetadataFingerprint(SunderPackageManifest source)
    {
        var value = JsonSerializer.SerializeToNode(CloneManifest(source, [], [], []), JsonOptions)!.AsObject();
        value["dependsOn"] = JsonSerializer.SerializeToNode((source.DependsOn ?? [])
            .OrderBy(static dependency => dependency.PackageId, StringComparer.Ordinal), JsonOptions);
        value["contractBundles"] = JsonSerializer.SerializeToNode((source.ContractBundles ?? [])
            .OrderBy(static bundle => bundle!.ContractId, StringComparer.Ordinal).ThenBy(static bundle => bundle!.Version, StringComparer.Ordinal), JsonOptions);
        return JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
    }

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

    private void WriteAggregate(
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
            if (sourceManifest.ContractBundles?.Any(bundle => bundle!.DescriptorPath == logicalPath) == true)
            {
                if (!HaveIdenticalFile(occurrences, logicalPath))
                    throw new InvalidDataException($"Aggregate contract descriptor '{logicalPath}' differs between target leaves.");
                Copy(occurrences[0].Files[logicalPath], SunderPackageFormat.SharedPayloadRoot + logicalPath, outputPath, destinations);
                continue;
            }
            if (IsExactRidNativeAsset(logicalPath, occurrences)
                || occurrences.Any(target =>
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

    private static bool IsExactRidNativeAsset(
        string logicalPath,
        IReadOnlyList<TargetLeaf> occurrences)
    {
        var segments = logicalPath.Split('/');
        if (segments.Length >= 5
            && string.Equals(segments[0], "lib", StringComparison.Ordinal)
            && string.Equals(segments[1], "runtimes", StringComparison.Ordinal)
            && string.Equals(segments[3], "native", StringComparison.Ordinal))
        {
            return true;
        }

        var fileName = Path.GetFileName(logicalPath);
        if (fileName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".node", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".so.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return !Path.HasExtension(fileName)
                   && occurrences.Any(target => HasNativeExecutableHeader(target.Files[logicalPath].FullPath));
        }

        return occurrences.Any(target => !HasManagedMetadata(target.Files[logicalPath].FullPath));
    }

    private static bool HasNativeExecutableHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            if (stream.Read(header) != header.Length)
            {
                return false;
            }

            if (header[0] == 0x7f && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
            {
                return true;
            }
            if (IsMachOHeader(header))
            {
                return true;
            }
            return header[0] == (byte)'M'
                   && header[1] == (byte)'Z'
                   && !HasManagedMetadata(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsMachOHeader(ReadOnlySpan<byte> header)
        => header is
            [0xfe, 0xed, 0xfa, 0xce]
            or [0xce, 0xfa, 0xed, 0xfe]
            or [0xfe, 0xed, 0xfa, 0xcf]
            or [0xcf, 0xfa, 0xed, 0xfe]
            or [0xca, 0xfe, 0xba, 0xbe]
            or [0xbe, 0xba, 0xfe, 0xca]
            or [0xca, 0xfe, 0xba, 0xbf]
            or [0xbf, 0xba, 0xfe, 0xca];

    private static bool HasManagedMetadata(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private void Copy(
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
        CancellationToken.ThrowIfCancellationRequested();
        using var input = File.OpenRead(source.FullPath);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long size = 0; int count;
        while ((count = input.Read(buffer)) != 0)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if ((size += count) > source.Size) throw new InvalidDataException("Aggregate input grew while copying its validated bytes.");
            hash.AppendData(buffer, 0, count); output.Write(buffer, 0, count);
        }
        if (size != source.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != source.Sha256)
            throw new InvalidDataException("Aggregate input bytes changed while copying their validated content identity.");
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
            PackageArtifactImports = source.PackageArtifactImports,
            PackageApiImports = source.PackageApiImports,
            ProviderImports = source.ProviderImports,
            PlatformImports = source.PlatformImports,
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
