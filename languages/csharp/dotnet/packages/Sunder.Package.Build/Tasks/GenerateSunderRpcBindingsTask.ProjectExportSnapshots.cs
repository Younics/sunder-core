using System.Security.Cryptography;
using Sunder.Package.Build.Rpc.Authoring;

namespace Sunder.Package.Build.Tasks;

public sealed partial class GenerateSunderRpcBindingsTask
{
    // An MSBuild target result can outlive the producer's mutable neutral tree:
    // another root/App evaluation may regenerate it before the consumer gets its
    // output lease. Return an immutable, hash-addressed handoff instead. Ordinary
    // Clean owns the mutable tree, not these still-referenced target results.
    private static string PublishImmutableProjectExport(string intermediateOutputPath, SourceAuthoringPlan plan)
    {
        var export = plan.Files.Single(file => file.RelativeOutputPath == SunderRpcAuthoringDocuments.PackageExportFileName);
        var projection = plan.Files.Single(file => file.RelativeOutputPath == SunderRpcAuthoringDocuments.ModelProjectionLogicalPath);
        var hash = Convert.ToHexStringLower(SHA256.HashData(export.Contents));
        var root = Path.Combine(intermediateOutputPath, "sunder-rpc-exports");
        GeneratedOutputPathSafety.EnsureSafeAncestors(root, includeLeaf: true, "RPC export snapshot cache");
        using var lease = GeneratedOutputLock.Acquire([root]);
        Directory.CreateDirectory(root);
        GeneratedOutputPathSafety.EnsureRegularDirectory(root, "RPC export snapshot cache");
        var ownership = Path.Combine(root, ".sunder-rpc-export-cache");
        var ownershipBytes = "sunder.rpc-project-export-cache.v1\n"u8.ToArray();
        if (!GeneratedOutputPathSafety.PathExists(ownership))
        {
            if (Directory.EnumerateFileSystemEntries(root).Any())
                throw new InvalidDataException("Refusing an unmarked nonempty RPC export snapshot cache.");
            WriteNewFile(ownership, ownershipBytes);
        }
        if (!SunderRpcDescriptorFileReader.ReadStableBytes(ownership, 1024, "RPC export cache marker").AsSpan().SequenceEqual(ownershipBytes))
            throw new InvalidDataException("The RPC export snapshot cache marker is invalid.");

        var snapshot = Path.Combine(root, hash);
        var generated = Path.Combine(snapshot, GeneratedDirectoryName);
        var path = Path.Combine(generated, SunderRpcAuthoringDocuments.PackageExportFileName);
        if (!GeneratedOutputPathSafety.PathExists(snapshot))
        {
            var stage = snapshot + ".stage-" + Guid.NewGuid().ToString("N");
            try
            {
                var stagedGenerated = Path.Combine(stage, GeneratedDirectoryName);
                Directory.CreateDirectory(stagedGenerated);
                WriteNewFile(Path.Combine(stagedGenerated, SunderRpcAuthoringDocuments.PackageExportFileName), export.Contents);
                WriteStagedFile(stagedGenerated, projection.RelativeOutputPath, projection.Contents);
                WriteNewFile(GetOwnershipMarkerPath(stagedGenerated), Utf8NoBom.GetBytes(MarkerContents));
                GeneratedOutputPathSafety.EnsureSafeAncestors(snapshot, includeLeaf: true, "RPC export snapshot publication");
                Directory.Move(stage, snapshot);
            }
            finally
            {
                if (Directory.Exists(stage))
                {
                    GeneratedOutputPathSafety.EnsureRegularDirectory(stage, "RPC export snapshot stage");
                    _ = ReadTree(stage);
                    DeleteStagedDirectory(stage);
                }
            }
        }
        GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "RPC export snapshot");
        GeneratedOutputPathSafety.EnsureRegularDirectory(snapshot, "RPC export snapshot");
        GeneratedOutputPathSafety.EnsureRegularDirectory(generated, "RPC export generated snapshot");
        if (!HasValidOwnershipMarker(GetOwnershipMarkerPath(generated))
            || !SunderRpcDescriptorFileReader.ReadStableBytes(path, 67_108_864, "RPC export snapshot").AsSpan().SequenceEqual(export.Contents)
            || !SunderRpcDescriptorFileReader.ReadStableBytes(Path.Combine(generated, projection.RelativeOutputPath), 4_194_304,
                "RPC export model projection snapshot").AsSpan().SequenceEqual(projection.Contents)
            || !ReadTree(generated).Where(entry => !entry.IsDirectory).Select(entry => entry.RelativePath.Replace('\\', '/')).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { export.RelativeOutputPath, projection.RelativeOutputPath }.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("The immutable RPC export snapshot does not match its content identity.");
        return path;
    }
}
