using System.Security.Cryptography;

namespace Sunder.Package.Build.Tasks;

internal sealed class SunderRpcInputSnapshot
{
    private readonly Dictionary<string, (int Length, string? Sha256)> _files = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public byte[] Read(string path, int maximumBytes, string label)
    {
        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, maximumBytes, label);
        Record(path, bytes);
        return bytes;
    }

    public void Record(string path, byte[] bytes)
        => Add(path, (bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));

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
    }

    public void Verify()
    {
        foreach (var (path, expected) in _files.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            try
            {
                GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "RPC authoring input");
                if (expected.Sha256 is null)
                {
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
