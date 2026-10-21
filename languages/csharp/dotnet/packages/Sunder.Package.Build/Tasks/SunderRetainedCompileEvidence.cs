using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Sdk.Storage;

namespace Sunder.Package.Build.Tasks;

// An inventory is not baseline authority. It can only account for exact compiler
// leaves separately from evidence discovery. All directories and non-Compile files
// still pass through the ordinary evidence walker, including unlisted/malformed
// evidence inside an otherwise valid immutable snapshot. Nothing persists across calls.
internal sealed class SunderRetainedCompileEvidence : IDisposable
{
    private readonly string _root;
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Length, string Sha256)> _proof = new(StringComparer.Ordinal);
    private int _entries;
    private long _bytes;
    private SunderBuildReadSnapshot? _reads;

    private SunderRetainedCompileEvidence(string root) => _root = root;

    internal static SunderRetainedCompileEvidence? TryRead(string root)
    {
        var directory = new DirectoryInfo(root);
        string descriptor, hash, marker, markerText;
        var roles = directory.Name == GenerateSunderRpcBindingsTask.GeneratedDirectoryName
            && directory.Parent?.Parent?.Name == "sunder-package-role-plans";
        if (roles)
        {
            hash = directory.Parent!.Name;
            descriptor = Path.Combine(root, "package-role-plan.v1.json");
            marker = Path.Combine(directory.Parent.Parent!.FullName, ".sunder-package-role-cache");
            markerText = "sunder.package-role-cache.v1\n";
        }
        else if (directory.Parent?.Name == "sunder-package-compilations"
                 && (directory.Name.StartsWith("worker-", StringComparison.Ordinal) || directory.Name.StartsWith("app-", StringComparison.Ordinal)))
        {
            hash = directory.Name[(directory.Name.IndexOf('-') + 1)..];
            descriptor = Path.Combine(root, SunderRoleCompilationSnapshot.DescriptorName);
            marker = Path.Combine(directory.Parent.FullName, ".sunder-role-compilation-cache");
            markerText = "sunder.role-compilation-cache.v1\n";
        }
        else return null;
        if (!HashText(hash) || !GeneratedOutputPathSafety.PathExists(marker) || !GeneratedOutputPathSafety.PathExists(descriptor)) return null;
        var result = new SunderRetainedCompileEvidence(root);
        if (!result.Read(marker, 1024).AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(markerText))) return null;
        if (roles)
        {
            var ownership = GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(root);
            if (!GeneratedOutputPathSafety.PathExists(ownership)
                || !result.Read(ownership, 1024).AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(GenerateSunderRpcBindingsTask.MarkerContents))) return null;
        }
        var bytes = result.Read(descriptor, 32 * 1024 * 1024);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash) return null;
        try
        {
            using var document = Sunder.Package.Format.SunderRpcAuthoringJson.ParseRegistryMetadata(bytes, 32 * 1024 * 1024);
            var value = document.RootElement;
            if (roles)
            {
                if (value.GetProperty("schemaVersion").GetInt32() != 1
                    || !result.Add(value.GetProperty("workerCompile")) || !result.Add(value.GetProperty("appCompile"))) return null;
            }
            else if (value.GetProperty("role").GetString() is not ("app" or "worker")
                     || !directory.Name.StartsWith(value.GetProperty("role").GetString() + "-", StringComparison.Ordinal)
                     || !result.Add(value.GetProperty("compile"))) return null;
            // A bounded historical frame owns its own read scope. It neither fills
            // the caller's current-source snapshot nor retains bytes between frames.
            result._reads = new SunderBuildReadSnapshot();
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null; // Unproven shapes retain ordinary discovery accounting.
        }
    }

    internal void CountEntry()
    {
        if (++_entries > 32_768) throw new InvalidDataException("rpc-authoring.source-limit-exceeded: Retained compilation snapshot exceeds its entry bound.");
    }

    internal bool VerifyFile(string path, FileAttributes attributes)
    {
        if (!_files.TryGetValue(Path.GetRelativePath(_root, path).Replace('\\', '/'), out var hash)) return false;
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new IOException("A retained compilation source is not a regular non-reparse file.");
        if ((attributes & FileAttributes.Directory) != 0) return false; // Still search an unexpected directory, even one named *.cs.
        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, 16 * 1024 * 1024, "retained compilation source");
        // Each role already has a 64 MiB source limit; a role plan can contain both.
        if ((_bytes += bytes.Length) > 128L * 1024 * 1024)
            throw new InvalidDataException("rpc-authoring.source-limit-exceeded: Retained compilation sources exceed their byte bound.");
        return Convert.ToHexStringLower(SHA256.HashData(bytes)) == hash;
    }

    internal void ReleaseInventory()
    {
        try { _reads?.Complete(); }
        catch (IOException) when (!GeneratedOutputPathSafety.PathExists(_root))
        {
            // Complete has already restored the caller's scope. A disappearing
            // whole historical output is harmless; unsafe ancestors still fail.
            GeneratedOutputPathSafety.EnsureSafeAncestors(_root, includeLeaf: true, "retained compilation evidence");
        }
        finally { _reads = null; _files.Clear(); }
    }

    public void Dispose() => ReleaseInventory();

    internal void Verify(Action<string>? beforeDirectoryRead)
    {
        beforeDirectoryRead?.Invoke(_root);
        GeneratedOutputPathSafety.EnsureSafeAncestors(_root, includeLeaf: true, "retained compilation evidence");
        if (!GeneratedOutputPathSafety.PathExists(_root)) return; // Clean may remove the entire historical output.
        GeneratedOutputPathSafety.EnsureRegularDirectory(_root, "retained compilation evidence");
        foreach (var (path, expected) in _proof)
            if (Convert.ToHexStringLower(SHA256.HashData(SunderRpcDescriptorFileReader.ReadCurrentBytes(path, expected.Length, "retained compilation proof"))) != expected.Sha256)
                throw new IOException("The retained compilation proof changed during authoring evidence discovery.");
    }

    private byte[] Read(string path, int maximumBytes)
    {
        var bytes = SunderRpcDescriptorFileReader.ReadCurrentBytes(path, maximumBytes, "retained compilation proof");
        _proof.Add(path, (bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        return bytes;
    }

    private bool Add(JsonElement files)
    {
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > 16_384) return false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object || file.EnumerateObject().Count() != 2
                || file.GetProperty("path").GetString() is not { } path || !PackageStorageValidation.IsValidRelativePath(path)
                || path.Contains('\\') || !path.EndsWith(".cs", StringComparison.Ordinal) || !seen.Add(path)
                || file.GetProperty("sha256").GetString() is not { } hash || !HashText(hash)) return false;
            if (!_files.TryAdd(path, hash) && _files[path] != hash) return false;
        }
        return true;
    }

    private static bool HashText(string? text)
        => text is { Length: 64 } && text.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
