using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Tasks;

/// <summary>Generates deterministic C# bindings and immutable canonical descriptor snapshots, including V2 ABI identity.</summary>
public sealed class GenerateSunderRpcBindingsTask : Microsoft.Build.Utilities.Task
{
    internal const string GeneratedDirectoryName = "sunder-rpc";
    internal const string SnapshotDirectoryName = ".snapshots";
    internal const string MarkerContents = "Sunder.Package.Build RPC generated output v1\n";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public ITaskItem[] Bindings { get; set; } = [];

    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    [Required]
    public string IntermediateOutputPath { get; set; } = string.Empty;

    [Output]
    public ITaskItem[] CompileItems { get; private set; } = [];

    [Output]
    public ITaskItem[] FileWrites { get; private set; } = [];

    [Output]
    public ITaskItem[] BindingSnapshots { get; private set; } = [];

    public override bool Execute()
    {
        CompileItems = [];
        FileWrites = [];
        BindingSnapshots = [];
        string? stagingPath = null;
        string? stagingMarkerPath = null;

        try
        {
            if (!TryResolveConfiguration(
                    out var projectDirectory,
                    out var intermediateOutputPath,
                    out var generatedDirectory,
                    out var markerPath))
            {
                return false;
            }

            var descriptorInputs = new Dictionary<string, SunderRpcDescriptorFileSnapshot>(PathComparer);
            var plans = BuildPlans(projectDirectory, generatedDirectory, descriptorInputs);
            if (plans is null) return false;

            var discoveryKey = GeneratedOutputLock.TargetLeafDiscoveryKey(
                intermediateOutputPath,
                generatedDirectory);
            using var outputLock = GeneratedOutputLock.Acquire([discoveryKey], [generatedDirectory]);

            GeneratedOutputPathSafety.EnsureRegularDirectory(
                intermediateOutputPath,
                "Sunder RPC intermediate output directory");
            GeneratedOutputPathSafety.EnsureSafeAncestors(
                generatedDirectory,
                includeLeaf: true,
                "Sunder RPC generated directory");
            GeneratedOutputPathSafety.EnsureSafeAncestors(
                markerPath,
                includeLeaf: true,
                "Sunder RPC generated-output marker");

            GeneratedOutputTransaction.RecoverAndCleanup(generatedDirectory, markerPath);
            if (!TryGetOwnedOutputState(generatedDirectory, markerPath, out var hasOwnedOutput))
            {
                return false;
            }
            if (!hasOwnedOutput && plans.Count == 0)
            {
                return true;
            }

            stagingPath = generatedDirectory + ".stage-" + Guid.NewGuid().ToString("N");
            stagingMarkerPath = markerPath + ".stage-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stagingPath);
            GeneratedOutputPathSafety.EnsureRegularDirectory(stagingPath, "staged Sunder RPC generated directory");

            foreach (var plan in plans.OrderBy(static plan => plan.OutputPath, StringComparer.Ordinal))
            {
                WriteStagedFile(stagingPath, plan.RelativeOutputPath, plan.SourceUtf8);
            }

            var snapshotPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            var snapshotContents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var input in descriptorInputs.Values.OrderBy(static input => input.SourcePath, PathComparer))
            {
                var relativePath = SnapshotRelativePath(input.Descriptor);
                if (snapshotContents.TryGetValue(relativePath, out var existingBytes))
                {
                    if (!existingBytes.AsSpan().SequenceEqual(input.CanonicalUtf8))
                    {
                        throw new InvalidDataException(
                            $"Sunder RPC descriptors produced a conflicting canonical snapshot '{relativePath}'.");
                    }
                }
                else
                {
                    WriteStagedFile(stagingPath, relativePath, input.CanonicalUtf8);
                    snapshotContents.Add(relativePath, input.CanonicalUtf8);
                }
                snapshotPaths.Add(input.SourcePath, Path.Combine(generatedDirectory, relativePath));
            }

            WriteNewFile(stagingMarkerPath, Utf8NoBom.GetBytes(MarkerContents));
            _ = ReadTree(stagingPath);
            GeneratedOutputPathSafety.EnsureRegularFile(
                stagingMarkerPath,
                "staged Sunder RPC generated-output marker");

            if (hasOwnedOutput
                && TreesAreByteIdentical(generatedDirectory, stagingPath)
                && HasValidOwnershipMarker(markerPath))
            {
                DeleteStagedDirectory(stagingPath);
                File.Delete(stagingMarkerPath);
            }
            else
            {
                GeneratedOutputPathSafety.EnsureRegularDirectory(
                    intermediateOutputPath,
                    "Sunder RPC intermediate output directory");
                GeneratedOutputTransaction.Commit(
                    new GeneratedOutputTransaction.StagedOutput(generatedDirectory, stagingPath),
                    new GeneratedOutputTransaction.StagedOutput(markerPath, stagingMarkerPath));
            }
            stagingPath = null;
            stagingMarkerPath = null;

            _ = ReadTree(generatedDirectory);
            if (!HasValidOwnershipMarker(markerPath))
            {
                throw new IOException($"Sunder RPC generated-output marker '{markerPath}' changed after publication.");
            }
            PublishTaskOutputs(plans, descriptorInputs.Values, snapshotPaths, generatedDirectory, markerPath);
            return true;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: false);
            return false;
        }
        finally
        {
            TryDeleteStagedDirectory(stagingPath);
            TryDeleteStagedFile(stagingMarkerPath);
        }
    }

    private bool TryResolveConfiguration(
        out string projectDirectory,
        out string intermediateOutputPath,
        out string generatedDirectory,
        out string markerPath)
    {
        projectDirectory = string.Empty;
        intermediateOutputPath = string.Empty;
        generatedDirectory = string.Empty;
        markerPath = string.Empty;
        if (string.IsNullOrWhiteSpace(ProjectDirectory) || string.IsNullOrWhiteSpace(IntermediateOutputPath))
        {
            Log.LogError("Sunder RPC binding generation requires ProjectDirectory and IntermediateOutputPath.");
            return false;
        }

        try
        {
            projectDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ProjectDirectory));
            intermediateOutputPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(IntermediateOutputPath, projectDirectory));
            GeneratedOutputPathSafety.EnsureRegularDirectory(projectDirectory, "Sunder RPC project directory");
            GeneratedOutputPathSafety.EnsureSafeAncestors(
                intermediateOutputPath,
                includeLeaf: true,
                "Sunder RPC intermediate output path");
            if (GeneratedOutputPathSafety.PathExists(intermediateOutputPath)
                && !Directory.Exists(intermediateOutputPath))
            {
                throw new IOException(
                    $"Sunder RPC IntermediateOutputPath '{IntermediateOutputPath}' must be a regular directory.");
            }
            if (Directory.Exists(intermediateOutputPath))
            {
                GeneratedOutputPathSafety.EnsureRegularDirectory(
                    intermediateOutputPath,
                    "Sunder RPC intermediate output directory");
            }

            generatedDirectory = Path.Combine(intermediateOutputPath, GeneratedDirectoryName);
            markerPath = GetOwnershipMarkerPath(generatedDirectory);
            GeneratedOutputPathSafety.EnsureSafeAncestors(
                generatedDirectory,
                includeLeaf: true,
                "Sunder RPC generated directory");
            GeneratedOutputPathSafety.EnsureSafeAncestors(
                markerPath,
                includeLeaf: true,
                "Sunder RPC generated-output marker");
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            Log.LogError($"Sunder RPC binding output configuration is invalid: {exception.Message}");
            return false;
        }
    }

    private List<BindingPlan>? BuildPlans(
        string projectDirectory,
        string generatedDirectory,
        IDictionary<string, SunderRpcDescriptorFileSnapshot> descriptorInputs)
    {
        var plans = new List<BindingPlan>(Bindings.Length);
        var outputOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in Bindings)
        {
            var namespaceName = binding.GetMetadata("Namespace").Trim();
            if (namespaceName.Length == 0)
            {
                Log.LogError($"SunderRpcBinding '{binding.ItemSpec}' requires 'Namespace' metadata.");
                continue;
            }
            if (!TryResolveAccessibility(binding, out var accessibility))
            {
                continue;
            }

            string descriptorPath;
            try
            {
                descriptorPath = Path.GetFullPath(binding.ItemSpec, projectDirectory);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Log.LogError($"SunderRpcBinding descriptor path '{binding.ItemSpec}' is invalid: {exception.Message}");
                continue;
            }

            if (!descriptorInputs.TryGetValue(descriptorPath, out var input))
            {
                try
                {
                    input = SunderRpcDescriptorFileReader.Read(
                        descriptorPath,
                        "SunderRpcBinding descriptor");
                    descriptorInputs.Add(descriptorPath, input);
                }
                catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or InvalidDataException
                                                  or SunderRpcDescriptorException)
                {
                    Log.LogError($"SunderRpcBinding descriptor '{binding.ItemSpec}' is invalid: {exception.Message}");
                    continue;
                }
            }

            string source;
            try
            {
                source = SunderRpcCSharpGenerator.Generate(input.Descriptor, namespaceName, accessibility);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                Log.LogError($"SunderRpcBinding '{binding.ItemSpec}' could not generate C# source: {exception.Message}");
                continue;
            }

            if (!TryResolveOutputPath(binding, input.Descriptor, generatedDirectory, out var relativeOutputPath, out var outputPath))
            {
                continue;
            }
            if (!outputOwners.TryAdd(outputPath, binding.ItemSpec))
            {
                Log.LogError(
                    $"SunderRpcBinding '{binding.ItemSpec}' and '{outputOwners[outputPath]}' resolve to the same output '{outputPath}'. "
                    + "Set distinct OutputFile metadata values.");
                continue;
            }
            plans.Add(new BindingPlan(
                binding.ItemSpec,
                relativeOutputPath,
                outputPath,
                Utf8NoBom.GetBytes(source)));
        }
        return Log.HasLoggedErrors ? null : plans;
    }

    private bool TryResolveAccessibility(
        ITaskItem binding,
        out SunderRpcCSharpAccessibility accessibility)
    {
        var value = binding.GetMetadata("Accessibility");
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "Public", StringComparison.Ordinal))
        {
            accessibility = SunderRpcCSharpAccessibility.Public;
            return true;
        }
        if (string.Equals(value, "Internal", StringComparison.Ordinal))
        {
            accessibility = SunderRpcCSharpAccessibility.Internal;
            return true;
        }

        accessibility = default;
        Log.LogError(
            $"SunderRpcBinding '{binding.ItemSpec}' Accessibility metadata '{value}' is invalid. "
            + "Allowed values are 'Public' and 'Internal'.");
        return false;
    }

    private bool TryResolveOutputPath(
        ITaskItem binding,
        SunderRpcContractDescriptor descriptor,
        string generatedDirectory,
        out string relativeOutputPath,
        out string outputPath)
    {
        relativeOutputPath = string.Empty;
        outputPath = string.Empty;
        var authoredOutputFile = binding.GetMetadata("OutputFile").Trim();
        var requestedOutputFile = authoredOutputFile.Length == 0
            ? DefaultOutputFile(descriptor)
            : authoredOutputFile;
        if (!TryNormalizeRelativeOutputFile(requestedOutputFile, out relativeOutputPath, out var error))
        {
            Log.LogError($"SunderRpcBinding '{binding.ItemSpec}' OutputFile '{authoredOutputFile}' is invalid: {error}");
            return false;
        }

        try
        {
            outputPath = Path.GetFullPath(Path.Combine(generatedDirectory, relativeOutputPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Log.LogError($"SunderRpcBinding '{binding.ItemSpec}' OutputFile '{authoredOutputFile}' is invalid: {exception.Message}");
            return false;
        }
        if (!GeneratedOutputPathSafety.IsDescendantOf(outputPath, generatedDirectory))
        {
            Log.LogError(
                $"SunderRpcBinding '{binding.ItemSpec}' OutputFile '{authoredOutputFile}' must remain under the owned generated directory.");
            return false;
        }
        return true;
    }

    private bool TryGetOwnedOutputState(string generatedDirectory, string markerPath, out bool hasOwnedOutput)
    {
        hasOwnedOutput = false;
        var outputExists = GeneratedOutputPathSafety.PathExists(generatedDirectory);
        var markerExists = GeneratedOutputPathSafety.PathExists(markerPath);
        if (outputExists)
        {
            try
            {
                GeneratedOutputPathSafety.EnsureRegularDirectory(
                    generatedDirectory,
                    "Sunder RPC generated directory");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.LogError($"Sunder RPC generated directory '{generatedDirectory}' is unsafe: {exception.Message}");
                return false;
            }
            if (!markerExists || !HasValidOwnershipMarker(markerPath))
            {
                Log.LogError(
                    $"Sunder RPC generated directory '{generatedDirectory}' already exists without its valid generated-output marker and will not be replaced.");
                return false;
            }
            hasOwnedOutput = true;
            return true;
        }
        if (markerExists)
        {
            Log.LogError(
                $"Sunder RPC generated-output marker '{markerPath}' exists without its generated directory; refusing unsafe replacement.");
            return false;
        }
        return true;
    }

    private void PublishTaskOutputs(
        IReadOnlyList<BindingPlan> plans,
        IEnumerable<SunderRpcDescriptorFileSnapshot> descriptorInputs,
        IReadOnlyDictionary<string, string> snapshotPaths,
        string generatedDirectory,
        string markerPath)
    {
        var compileItems = new List<ITaskItem>(plans.Count);
        foreach (var plan in plans.OrderBy(static plan => plan.OutputPath, StringComparer.Ordinal))
        {
            var item = new TaskItem(plan.OutputPath);
            item.SetMetadata("AutoGen", "true");
            item.SetMetadata("DesignTime", "true");
            item.SetMetadata("Visible", "false");
            compileItems.Add(item);
        }

        var snapshots = new List<ITaskItem>();
        foreach (var input in descriptorInputs.OrderBy(static input => input.SourcePath, PathComparer))
        {
            var descriptor = input.Descriptor;
            var item = new TaskItem(snapshotPaths[input.SourcePath]);
            item.SetMetadata("SourceDescriptorPath", input.SourcePath);
            item.SetMetadata("GeneratedDirectory", generatedDirectory);
            item.SetMetadata("OwnershipMarkerPath", markerPath);
            item.SetMetadata("DescriptorVersion", descriptor.DescriptorVersion.ToString(CultureInfo.InvariantCulture));
            item.SetMetadata("ContractId", descriptor.ContractId);
            item.SetMetadata("ContractVersion", descriptor.Version);
            item.SetMetadata("DescriptorSha256", descriptor.Sha256);
            item.SetMetadata("AbiSha256", descriptor.AbiSha256 ?? string.Empty);
            snapshots.Add(item);
        }

        CompileItems = compileItems.ToArray();
        BindingSnapshots = snapshots.ToArray();
        FileWrites = compileItems
            .Select(static item => item.ItemSpec)
            .Concat(snapshotPaths.Values)
            .Distinct(PathComparer)
            .Order(PathComparer)
            .Select(static path => (ITaskItem)new TaskItem(path))
            .ToArray();
    }

    internal static string GetOwnershipMarkerPath(string generatedDirectory)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(generatedDirectory))
           + ValidateSunderDevOutputPathTask.MarkerFileName;

    internal static bool HasValidOwnershipMarker(string markerPath)
    {
        try
        {
            var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(
                markerPath,
                1024,
                "Sunder RPC generated-output marker");
            return bytes.AsSpan().SequenceEqual(Utf8NoBom.GetBytes(MarkerContents));
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException)
        {
            return false;
        }
    }

    private static string SnapshotRelativePath(SunderRpcContractDescriptor descriptor)
        => Path.Combine(SnapshotDirectoryName, descriptor.Sha256 + ".rpc.json");

    private static void WriteStagedFile(string stagingRoot, string relativePath, ReadOnlySpan<byte> bytes)
    {
        var outputPath = Path.GetFullPath(Path.Combine(stagingRoot, relativePath));
        if (!GeneratedOutputPathSafety.IsDescendantOf(outputPath, stagingRoot))
        {
            throw new InvalidDataException($"Staged Sunder RPC output '{relativePath}' escapes its owned generated directory.");
        }
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);
        GeneratedOutputPathSafety.EnsureRegularDirectory(outputDirectory, "staged Sunder RPC output directory");
        WriteNewFile(outputPath, bytes);
        GeneratedOutputPathSafety.EnsureRegularFile(outputPath, "staged Sunder RPC output");
    }

    private static void WriteNewFile(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static bool TreesAreByteIdentical(string leftRoot, string rightRoot)
    {
        var left = ReadTree(leftRoot);
        var right = ReadTree(rightRoot);
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].RelativePath, right[index].RelativePath, StringComparison.Ordinal)
                || left[index].IsDirectory != right[index].IsDirectory
                || !left[index].Contents.AsSpan().SequenceEqual(right[index].Contents))
            {
                return false;
            }
        }
        return true;
    }

    private static IReadOnlyList<TreeEntry> ReadTree(string root)
    {
        GeneratedOutputPathSafety.EnsureRegularDirectory(root, "Sunder RPC generated directory");
        var entries = new List<TreeEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(current).Order(PathComparer))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new IOException($"Sunder RPC generated output '{path}' is not a regular file or directory.");
                }
                var relativePath = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    entries.Add(new TreeEntry(relativePath, true, []));
                    pending.Push(path);
                }
                else
                {
                    entries.Add(new TreeEntry(
                        relativePath,
                        false,
                        SunderRpcDescriptorFileReader.ReadStableBytes(
                            path,
                            int.MaxValue,
                            "Sunder RPC generated output")));
                }
            }
        }
        return entries.OrderBy(static entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static string DefaultOutputFile(SunderRpcContractDescriptor descriptor)
    {
        var identity = Utf8NoBom.GetBytes(descriptor.ContractId + "\0" + descriptor.Version);
        var identityHash = Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
        return $"SunderRpc.{identityHash}.g.cs";
    }

    private static bool TryNormalizeRelativeOutputFile(string value, out string normalized, out string error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "It must be a non-empty relative .cs path.";
            return false;
        }
        if (value[0] is '/' or '\\' || Path.IsPathRooted(value))
        {
            error = "It must be relative.";
            return false;
        }

        var portablePath = value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var segments = portablePath.Split(Path.DirectorySeparatorChar);
        const string portableInvalidCharacters = "<>:\"|?*";
        if (segments.Any(segment => segment.Length == 0
                                    || segment is "." or ".."
                                    || segment.EndsWith(' ')
                                    || segment.EndsWith('.')
                                    || segment.Any(character => char.IsControl(character)
                                                                || portableInvalidCharacters.Contains(character)
                                                                || Path.GetInvalidFileNameChars().Contains(character))))
        {
            error = "It contains an unsafe or non-portable path segment.";
            return false;
        }
        if (!string.Equals(Path.GetExtension(portablePath), ".cs", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(portablePath)))
        {
            error = "It must identify a .cs file.";
            return false;
        }

        normalized = portablePath;
        error = string.Empty;
        return true;
    }

    private static void DeleteStagedDirectory(string path)
    {
        GeneratedOutputPathSafety.EnsureRegularDirectory(path, "staged Sunder RPC generated directory");
        Directory.Delete(path, recursive: true);
    }

    private static void TryDeleteStagedDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (!GeneratedOutputPathSafety.PathExists(path)) return;
            if (Directory.Exists(path) && !GeneratedOutputPathSafety.IsReparsePoint(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A failed stage is outside the published ownership pair and can be collected later.
        }
    }

    private static void TryDeleteStagedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (!GeneratedOutputPathSafety.PathExists(path)) return;
            if (Directory.Exists(path) && !GeneratedOutputPathSafety.IsReparsePoint(path)) return;
            File.Delete(path);
        }
        catch
        {
            // A failed marker stage is outside the published ownership pair and can be collected later.
        }
    }

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record BindingPlan(
        string ItemSpec,
        string RelativeOutputPath,
        string OutputPath,
        byte[] SourceUtf8);

    private sealed record TreeEntry(string RelativePath, bool IsDirectory, byte[] Contents);
}
