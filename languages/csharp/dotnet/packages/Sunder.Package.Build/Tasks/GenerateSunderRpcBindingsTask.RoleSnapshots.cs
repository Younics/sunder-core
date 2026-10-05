using System.Security.Cryptography;

namespace Sunder.Package.Build.Tasks;

public sealed partial class GenerateSunderRpcBindingsTask
{
    // MSBuild imports restore props/targets during evaluation, before a child task
    // can take its compilation lease. Handoff uses the same immutable-cache model
    // as project exports, so Clean/regeneration cannot change those evaluated bytes.
    private static string PublishImmutableRolePlan(SourceAuthoringPlan plan, string generated)
        => PublishImmutableGeneratedPlan(plan, generated, RolePlanPath, "sunder-package-role-plans", ".sunder-package-role-cache", "sunder.package-role-cache.v1\n");

    private static string PublishImmutableGeneratedPlan(SourceAuthoringPlan plan, string generated, string descriptorPath, string cacheName, string markerName, string markerText)
    {
        var descriptor = plan.Files.Single(file => file.RelativeOutputPath == descriptorPath);
        var hash = Convert.ToHexStringLower(SHA256.HashData(descriptor.Contents));
        var root = Path.Combine(Path.GetDirectoryName(generated)!, cacheName);
        GeneratedOutputPathSafety.EnsureSafeAncestors(root, includeLeaf: true, "package role snapshot cache");
        using var lease = GeneratedOutputLock.Acquire([root]);
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, markerName);
        var markerBytes = Utf8NoBom.GetBytes(markerText);
        if (!GeneratedOutputPathSafety.PathExists(marker))
        {
            if (Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidDataException("Refusing an unmarked package role snapshot cache.");
            WriteNewFile(marker, markerBytes);
        }
        if (!SunderRpcDescriptorFileReader.ReadStableBytes(marker, 1024, "package role cache marker").AsSpan().SequenceEqual(markerBytes))
            throw new InvalidDataException("The package role snapshot cache marker is invalid.");
        var snapshot = Path.Combine(root, hash);
        var output = Path.Combine(snapshot, GeneratedDirectoryName);
        if (!Directory.Exists(snapshot))
        {
            var stage = snapshot + ".stage-" + Guid.NewGuid().ToString("N");
            try
            {
                var staged = Path.Combine(stage, GeneratedDirectoryName);
                Directory.CreateDirectory(staged);
                foreach (var entry in ReadTree(generated))
                    if (entry.IsDirectory) Directory.CreateDirectory(Path.Combine(staged, entry.RelativePath));
                    else WriteStagedFile(staged, entry.RelativePath, entry.Contents);
                WriteNewFile(GetOwnershipMarkerPath(staged), Utf8NoBom.GetBytes(MarkerContents));
                Directory.Move(stage, snapshot);
            }
            finally { TryDeleteStagedDirectory(stage); }
        }
        GeneratedOutputPathSafety.EnsureSafeAncestors(output, includeLeaf: true, "package role snapshot");
        if (!HasValidOwnershipMarker(GetOwnershipMarkerPath(output)) || !TreesAreByteIdentical(generated, output))
            throw new InvalidDataException("The immutable package role snapshot does not match its content identity.");
        return output;
    }
}
