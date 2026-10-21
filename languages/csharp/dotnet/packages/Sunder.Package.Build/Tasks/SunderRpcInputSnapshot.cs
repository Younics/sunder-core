using System.Security.Cryptography;

namespace Sunder.Package.Build.Tasks;

internal sealed class SunderRpcInputSnapshot
{
    private readonly Dictionary<string, (int Length, string? Sha256)> _files = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, (string[] Paths, Func<IEnumerable<string>> Read)> _memberships = new(StringComparer.Ordinal);

    public void CaptureMembership(string name, Func<IEnumerable<string>> read)
    {
        var paths = read().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (_memberships.TryGetValue(name, out var previous) && !previous.Paths.SequenceEqual(paths, StringComparer.Ordinal)) throw Changed(name);
        _memberships[name] = (paths, read);
    }

    // The evaluated Compile set is authoritative. Directory membership is also
    // captured so a newly added glob/linked input cannot hide behind that already
    // evaluated list. A changed set requires a fresh MSBuild evaluation.
    internal void CaptureSourceMembership(string projectDirectory, Func<IEnumerable<string>> evaluatedPaths, IEnumerable<string> excludedDirectories)
    {
        var project = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(projectDirectory);
        var exclusions = excludedDirectories.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(Path.GetFullPath(path, project))).ToArray();
        CaptureMembership("evaluated Compile inputs", () => evaluatedPaths().Select(GeneratedOutputPathSafety.NormalizeTrustedSystemAliases));
        var roots = evaluatedPaths().Select(path => Path.GetDirectoryName(GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(path))!)
            .Where(path => !GeneratedOutputPathSafety.IsDescendantOf(path, project) && path != project).Append(project)
            .Distinct(_files.Comparer).ToArray();
        CaptureMembership("authored source discovery membership", () => Discover(roots, exclusions));
    }

    private static IEnumerable<string> Discover(IEnumerable<string> roots, IReadOnlyList<string> excluded)
    {
        var pending = new Stack<(string Path, int Depth)>(roots.Select(path => (path, 0)));
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            if (!visited.Add(directory.Path)) continue;
            if (directory.Depth > 32) throw Changed("source discovery depth exceeds 32");
            GeneratedOutputPathSafety.EnsureRegularDirectory(directory.Path, "RPC source membership");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                if (++count > 32768) throw Changed("source discovery membership exceeds 32,768 entries");
                var name = Path.GetFileName(path);
                if (name is "obj" or "bin" or ".git" or "node_modules" or ".sunder" || excluded.Any(root =>
                    path == root || GeneratedOutputPathSafety.IsDescendantOf(path, root))) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    pending.Push((path, directory.Depth + 1));
                }
                else if (Path.GetExtension(path) is ".cs" or ".csproj" or ".props" or ".targets") yield return path;
            }
        }
    }

    public byte[] Read(string path, int maximumBytes, string label)
    {
        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, maximumBytes, label);
        Record(path, bytes);
        return bytes;
    }

    public void Record(string path, byte[] bytes)
        => Add(path, (bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));

    internal string CapturedSha256(string path)
        => _files.TryGetValue(GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(path), out var file) && file.Sha256 is { } hash
            ? hash.ToLowerInvariant() : throw Changed("uncaptured source '" + path + "'");

    public void ExpectAbsent(string path)
    {
        GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "RPC authoring input");
        if (GeneratedOutputPathSafety.PathExists(path)) throw Changed(path);
        Add(path, (0, null));
    }

    public void Merge(SunderRpcInputSnapshot other, bool requireSameFiles = false)
    {
        if (requireSameFiles && (_files.Count != other._files.Count || _files.Keys.Any(path => !other._files.ContainsKey(path))))
            throw Changed("evaluated RPC input set");
        foreach (var (path, expected) in other._files) Add(path, expected);
        if (requireSameFiles && !_memberships.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(other._memberships.Keys))
            throw Changed("evaluated RPC membership capture");
        foreach (var (name, membership) in other._memberships)
        {
            if (_memberships.TryGetValue(name, out var previous) && !previous.Paths.SequenceEqual(membership.Paths, StringComparer.Ordinal)) throw Changed(name);
            _memberships[name] = membership;
        }
    }

    public void Verify()
    {
        // Verification must start with current bytes even inside an analysis
        // snapshot. Its own bounded read scope shares ancestry work and rereads
        // every retained input before returning to the publication boundary.
        using var reads = new SunderBuildReadSnapshot();
        foreach (var (name, membership) in _memberships)
            if (!membership.Paths.SequenceEqual(membership.Read().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw Changed(name);
        foreach (var (path, expected) in _files.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            try
            {
                if (expected.Sha256 is null)
                {
                    GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "RPC authoring input");
                    if (GeneratedOutputPathSafety.PathExists(path)) throw Changed(path);
                    continue;
                }
                var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, expected.Length, "RPC authoring input");
                if (bytes.Length != expected.Length || Convert.ToHexString(SHA256.HashData(bytes)) != expected.Sha256)
                    throw Changed(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw Changed(path, exception);
            }
        }
    }

    private void Add(string path, (int Length, string? Sha256) expected)
    {
        path = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(path);
        if (_files.TryGetValue(path, out var previous) && previous != expected) throw Changed(path);
        _files[path] = expected;
    }

    private static IOException Changed(string path, Exception? inner = null)
        => new($"RPC authoring input '{path}' changed after it was read. No proposal was published; re-run against the current inputs.", inner);
}
