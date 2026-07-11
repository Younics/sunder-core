using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Tasks;

internal static class PackageContentIndexer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static void Write(string stagingPath)
    {
        var files = Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories)
            .Where(static path => !string.Equals(Path.GetFileName(path), "content-index.json", StringComparison.OrdinalIgnoreCase))
            .Select(path => CreateEntry(stagingPath, path))
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        var outputPath = Path.Combine(stagingPath, "manifest", "content-index.json");
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new SunderPackageContentIndex(1, files), JsonOptions) + "\n");
    }

    private static SunderPackageContentIndexEntry CreateEntry(string stagingPath, string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var relativePath = Path.GetRelativePath(stagingPath, filePath).Replace('\\', '/');
        return new SunderPackageContentIndexEntry(
            relativePath,
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
            stream.Length,
            ResolveRole(relativePath));
    }

    private static string ResolveRole(string path)
    {
        if (path.StartsWith("payload/lib/runtimes/", StringComparison.OrdinalIgnoreCase)) return "native";
        if (path.StartsWith("payload/lib/", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)) return "assembly";
        if (path.StartsWith("payload/assets/", StringComparison.OrdinalIgnoreCase)) return "asset";
        if (path.StartsWith("manifest/", StringComparison.OrdinalIgnoreCase)) return "manifest";
        return "file";
    }
}
