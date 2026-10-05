using Microsoft.Build.Framework;

namespace Sunder.Package.Build.Tasks;

public sealed class CleanSunderRpcGeneratedOutputTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    public ITaskItem[] GeneratedDirectories { get; set; } = [];

    public override bool Execute()
    {
        try
        {
            var projectDirectory = Path.GetFullPath(ProjectDirectory);
            var outputs = GeneratedDirectories
                .Select(item => ResolveOutput(item, projectDirectory))
                .DistinctBy(static output => output.GeneratedDirectory, PathComparer)
                .OrderBy(static output => output.GeneratedDirectory, PathComparer)
                .ToArray();
            var discoveryKeys = outputs.Select(static output => GeneratedOutputLock.TargetLeafDiscoveryKey(
                output.DiscoveryRoot,
                output.GeneratedDirectory));
            using var outputLock = GeneratedOutputLock.Acquire(
                discoveryKeys,
                outputs.Select(static output => output.GeneratedDirectory));

            foreach (var output in outputs)
            {
                Clean(output);
            }
            return true;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: false);
            return false;
        }
    }

    private static OutputPath ResolveOutput(ITaskItem item, string projectDirectory)
    {
        var discoveryRootValue = item.GetMetadata("DiscoveryRoot");
        if (string.IsNullOrWhiteSpace(discoveryRootValue))
        {
            throw new InvalidDataException(
                $"RPC generated-output directory '{item.ItemSpec}' is missing DiscoveryRoot metadata.");
        }
        var discoveryRoot = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(
            Path.GetFullPath(discoveryRootValue, projectDirectory));
        if (!GeneratedOutputPathSafety.TryValidateDirectory(
                item.ItemSpec,
                projectDirectory,
                discoveryRoot,
                GenerateSunderRpcBindingsTask.GeneratedDirectoryName,
                out var generatedDirectory,
                out var error))
        {
            throw new InvalidDataException(
                $"RPC generated-output directory '{item.ItemSpec}' is unsafe. {error}");
        }
        return new(generatedDirectory, discoveryRoot);
    }

    private static void Clean(OutputPath output)
    {
        var generatedDirectory = output.GeneratedDirectory;
        var markerPath = GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(generatedDirectory);
        GeneratedOutputPathSafety.EnsureSafeAncestors(
            generatedDirectory,
            includeLeaf: true,
            "Sunder RPC generated directory");
        GeneratedOutputPathSafety.EnsureSafeAncestors(
            markerPath,
            includeLeaf: true,
            "Sunder RPC generated-output marker");
        GeneratedOutputTransaction.RecoverAndCleanup(generatedDirectory, markerPath);
        EnsureNoTransactionArtifacts(generatedDirectory, markerPath);

        var directoryExists = GeneratedOutputPathSafety.PathExists(generatedDirectory);
        var markerExists = GeneratedOutputPathSafety.PathExists(markerPath);
        if (!directoryExists && !markerExists) return;
        if (!directoryExists)
        {
            GeneratedOutputPathSafety.EnsureRegularFile(
                markerPath,
                "Sunder RPC generated-output marker");
            if (!GenerateSunderRpcBindingsTask.HasValidOwnershipMarker(markerPath))
            {
                throw new InvalidDataException(
                    $"RPC generated-output marker '{markerPath}' is invalid and will not be deleted.");
            }
            File.Delete(markerPath);
            return;
        }
        if (!markerExists)
        {
            throw new InvalidDataException(
                $"RPC generated output '{generatedDirectory}' is incomplete and will not be deleted.");
        }

        GeneratedOutputPathSafety.EnsureRegularDirectory(
            generatedDirectory,
            "Sunder RPC generated directory");
        GeneratedOutputPathSafety.EnsureRegularFile(
            markerPath,
            "Sunder RPC generated-output marker");
        if (!GenerateSunderRpcBindingsTask.HasValidOwnershipMarker(markerPath))
        {
            throw new InvalidDataException(
                $"RPC generated output '{generatedDirectory}' has no valid ownership marker and will not be deleted.");
        }
        EnsureTreeContainsNoLinks(generatedDirectory);

        Directory.Delete(generatedDirectory, recursive: true);
        File.Delete(markerPath);
    }

    private static void EnsureNoTransactionArtifacts(string generatedDirectory, string markerPath)
    {
        var candidates = new[]
        {
            generatedDirectory + GeneratedOutputTransaction.TransactionSuffix,
            generatedDirectory + GeneratedOutputTransaction.TransactionSuffix + ".installed",
            markerPath + GeneratedOutputTransaction.TransactionSuffix,
            markerPath + GeneratedOutputTransaction.TransactionSuffix + ".installed",
        };
        if (candidates.Any(GeneratedOutputPathSafety.PathExists)
            || EnumerateMatchingSiblings(generatedDirectory, ".stage-").Any()
            || EnumerateMatchingSiblings(generatedDirectory, ".backup-").Any()
            || EnumerateMatchingSiblings(markerPath, ".stage-").Any()
            || EnumerateMatchingSiblings(markerPath, ".backup-").Any())
        {
            throw new InvalidDataException(
                $"RPC generated output '{generatedDirectory}' has an incomplete transaction and will not be deleted.");
        }
    }

    private static IEnumerable<string> EnumerateMatchingSiblings(string path, string suffix)
    {
        var parent = Path.GetDirectoryName(path);
        return parent is null || !Directory.Exists(parent)
            ? []
            : Directory.EnumerateFileSystemEntries(parent, Path.GetFileName(path) + suffix + "*");
    }

    private static void EnsureTreeContainsNoLinks(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"RPC generated output '{root}' contains symbolic link or reparse point '{entry}' and will not be deleted.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else if ((attributes & FileAttributes.Device) != 0)
                {
                    throw new InvalidDataException(
                        $"RPC generated output '{root}' contains non-regular file '{entry}' and will not be deleted.");
                }
            }
        }
    }

    private sealed record OutputPath(string GeneratedDirectory, string DiscoveryRoot);

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
