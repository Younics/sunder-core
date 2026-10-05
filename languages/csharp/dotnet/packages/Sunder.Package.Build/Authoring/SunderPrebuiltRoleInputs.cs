using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Authoring;

internal sealed record SunderPrebuiltFile(string Path, long Bytes, string Sha256);
internal sealed record SunderPrebuiltItem(string Kind, string Identity, SortedDictionary<string, string> Metadata);
internal sealed record SunderPrebuiltFrame(string PackageId, string PackageVersion, string Project, string ProjectIdentity,
    string Configuration, string TargetFramework, string RuntimeIdentifier, string AssemblyName,
    string OwnedBaseline, string OwnedBaselineSha256, string ContractLock, string ContractLockSha256,
    SortedDictionary<string, string> Properties, SunderPrebuiltItem[] Items, SunderPrebuiltFile[] Sources,
    SunderPrebuiltFile[] Toolset, SunderPrebuiltFile[] ExternalInputs);
internal sealed record SunderPrebuiltRole(string Role, string[] Libraries, SunderPrebuiltFile[] GeneratedInputs,
    SunderPrebuiltFile[] References, SunderPrebuiltFile[] RuntimeReferences, string Receipt);
internal sealed record SunderPrebuiltPlan(string InputFingerprint, string NativePlanSha256, bool HasApp, SunderPrebuiltRole[] Roles);
internal sealed record SunderPrebuiltEvidence(int SchemaVersion, SunderPrebuiltFrame Inputs, SunderPrebuiltPlan RolePlan,
    string Leaf, SunderPrebuiltFile[] Files);
internal sealed record SunderPrebuiltSelectionEntry(string RuntimeIdentifier, string Path, string Sha256);
internal sealed record SunderPrebuiltSelection(int SchemaVersion, SunderPrebuiltSelectionEntry[] Inputs);

internal static class SunderPrebuiltRoleInputs
{
    internal const string FileName = "role-inputs.v1.json";
    internal const int MaximumDocumentBytes = 64 * 1024 * 1024;
    internal const int MaximumFiles = 16384;
    internal const long MaximumSourceBytes = 512L * 1024 * 1024;
    internal const long MaximumLeafBytes = 2L * 1024 * 1024 * 1024;
    internal static readonly string[] Rids = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 16,
        Converters = { new OrdinalStringMapConverter() },
    };
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".idea", ".opencode", "bin", "obj", "artifacts", "TestResults", "node_modules", ".sunder" };
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".csproj", ".props", ".targets", ".json", ".axaml", ".xaml", ".paml", ".resx", ".ruleset", ".config", ".editorconfig" };

    internal static InvalidDataException Invalid(string message) => new("package-authoring.prebuilt-input-invalid: " + message);
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static void RequireHash(string value)
    {
        if (value is not { Length: 64 } || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Invalid("An exact lowercase SHA-256 is required.");
    }
    internal static byte[] Write<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumDocumentBytes) throw Invalid("Role-input evidence exceeds 64 MiB.");
        return bytes;
    }
    internal static T Read<T>(byte[] bytes, int maximum = MaximumDocumentBytes)
    {
        // Shared preflight rejects duplicate/case-colliding members, alternate
        // numbers, invalid UTF-8 and excessive depth/structural allocation.
        using var document = SunderRpcAuthoringJson.ParseRegistryMetadata(bytes, maximum);
        var value = document.RootElement.Deserialize<T>(Json) ?? throw Invalid("Empty role-input document.");
        if (!Write(value).AsSpan().SequenceEqual(bytes)) throw Invalid("Role-input documents require their exact compact closed JSON representation.");
        return value;
    }

    // SortedDictionary's default deserialization comparer is culture-sensitive.
    // Evidence and private capture maps must preserve the same ordinal ordering
    // on native producers and fan-in hosts, including punctuation and casing.
    private sealed class OrdinalStringMapConverter : JsonConverter<SortedDictionary<string, string>>
    {
        public override SortedDictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected a string map.");
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a map key.");
                var key = reader.GetString()!;
                if (!reader.Read() || reader.TokenType != JsonTokenType.String || !result.TryAdd(key, reader.GetString()!))
                    throw new JsonException("Expected unique string-valued map entries.");
            }
            if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException("Incomplete string map.");
            return result;
        }

        public override void Write(Utf8JsonWriter writer, SortedDictionary<string, string> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var entry in value.OrderBy(entry => entry.Key, StringComparer.Ordinal)) writer.WriteString(entry.Key, entry.Value);
            writer.WriteEndObject();
        }
    }
    internal static string Full(string path) => GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(path);
    internal static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(Full(root), Full(path)).Replace('\\', '/');
        if (!ArchiveRelativePath.TryParse(relative, 1024, 32, out _, out _)) throw Invalid("An authored input escapes the reviewed source root; set SunderPackageRoleInputSourceRoot explicitly.");
        return relative;
    }
    internal static string Child(string root, string relative)
        => ArchiveRelativePath.Parse(relative).ToPlatformPath(root);

    internal static SunderPrebuiltFile File(string path, string identity, long maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        GeneratedOutputPathSafety.EnsureRegularFile(path, "qualified role input");
        var before = new FileInfo(path); var state = (before.Length, before.CreationTimeUtc, before.LastWriteTimeUtc, before.Attributes);
        if (before.Length > maximum) throw Invalid("A qualified input exceeds its byte budget.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long length = 0;
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested(); length += count;
            if (length > maximum) throw Invalid("An input grew beyond its byte budget.");
            hash.AppendData(buffer, 0, count);
        }
        var after = new FileInfo(path); GeneratedOutputPathSafety.EnsureRegularFile(path, "qualified role input");
        if (state != (after.Length, after.CreationTimeUtc, after.LastWriteTimeUtc, after.Attributes) || length != state.Length)
            throw Invalid("An input changed while its bytes were captured.");
        return new(identity, length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    internal static SunderPrebuiltFile[] Tree(string root, bool source, CancellationToken token)
    {
        root = Full(root); GeneratedOutputPathSafety.EnsureRegularDirectory(root, "qualified input root");
        var result = new List<SunderPrebuiltFile>(); var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0));
        long bytes = 0; var entries = 0;
        while (pending.TryPop(out var directory)) foreach (var path in Directory.EnumerateFileSystemEntries(directory.Path).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (++entries > 65536 || directory.Depth > 32) throw Invalid("Input membership exceeds its discovery budget.");
            var name = Path.GetFileName(path); var attributes = System.IO.File.GetAttributes(path);
            if (source && IgnoredDirectories.Contains(name) && (attributes & FileAttributes.Directory) != 0) continue;
            if (source && (name.EndsWith(GeneratedOutputLock.LockSuffix, StringComparison.Ordinal)
                || name.Contains(GeneratedOutputLock.LockSuffix + ".", StringComparison.Ordinal))) continue;
            if (source && name == ".git") continue;
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw Invalid("Qualified inputs cannot contain links/reparse points.");
            if ((attributes & FileAttributes.Directory) != 0) { pending.Push((path, directory.Depth + 1)); continue; }
            if (source && !SourceExtensions.Contains(Path.GetExtension(path)) && name != ".editorconfig") continue;
            var relative = Relative(root, path);
            if (!folded.Add(relative) || result.Count == MaximumFiles) throw Invalid("Input paths collide or exceed the file budget.");
            var file = File(path, relative, source ? 64 * 1024 * 1024 : MaximumLeafBytes, token);
            bytes += file.Bytes;
            if (bytes > (source ? MaximumSourceBytes : MaximumLeafBytes)) throw Invalid("Input tree exceeds its aggregate byte budget.");
            result.Add(file);
        }
        return result.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }
    internal static void Equal<T>(T expected, T actual, string label)
    {
        var before = Write(expected); var after = Write(actual);
        if (before.AsSpan().SequenceEqual(after)) return;
        using var left = JsonDocument.Parse(before); using var right = JsonDocument.Parse(after);
        throw Invalid(label + " changed or differs from the reviewed native input at " + Difference(left.RootElement, right.RootElement, "$") + ".");

        static string Difference(JsonElement left, JsonElement right, string path)
        {
            if (left.ValueKind != right.ValueKind) return path;
            if (left.ValueKind == JsonValueKind.Object)
                foreach (var property in left.EnumerateObject())
                    if (!right.TryGetProperty(property.Name, out var value)) return path + "." + property.Name;
                    else if (!JsonElement.DeepEquals(property.Value, value)) return Difference(property.Value, value, path + "." + property.Name);
            if (left.ValueKind == JsonValueKind.Array)
            {
                if (left.GetArrayLength() != right.GetArrayLength()) return path + ".count";
                for (var i = 0; i < left.GetArrayLength(); i++) if (!JsonElement.DeepEquals(left[i], right[i])) return Difference(left[i], right[i], path + "[" + i + "]");
            }
            return path;
        }
    }
}
