using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Sdk.Storage;

namespace Sunder.Package.Build.Tasks;

// Full-package analysis/evidence still runs on every build. Only the compiler's immutable
// inputs have role-local identities, so a sibling role edit does not invalidate Csc/AXAML.
internal static class SunderRoleCompilationSnapshot
{
    internal const string DescriptorName = "compilation-inputs.v1.json";
    private const string MarkerName = ".sunder-role-compilation-cache";
    private static readonly byte[] Marker = "sunder.role-compilation-cache.v1\n"u8.ToArray();

    internal static string Publish(JsonElement plan, string planRoot, string role)
    {
        if (!plan.TryGetProperty("compilationCacheRoot", out var cache)) return planRoot;
        var root = cache.GetString()!;
        if (!Path.IsPathFullyQualified(root) || role is not ("app" or "worker"))
            throw new InvalidDataException("The role compilation cache identity is invalid.");
        var resources = plan.GetProperty("resources").EnumerateArray()
            .Where(value => value.GetProperty("roles").GetString() is "shared" || value.GetProperty("roles").GetString() == role).ToArray();
        var dependencies = plan.GetProperty(role + "Dependencies");
        var descriptor = JsonSerializer.SerializeToUtf8Bytes(new
        {
            role, projectIdentity = plan.GetProperty("projectIdentity"), assemblyName = plan.GetProperty("assemblyName"),
            configuration = plan.GetProperty("configuration"), targetFramework = plan.GetProperty("targetFramework"),
            runtimeIdentifier = plan.GetProperty("runtimeIdentifier"), packageVersion = plan.GetProperty("packageVersion"),
            defineConstants = plan.GetProperty("defineConstants"), compile = plan.GetProperty(role + "Compile"),
            references = plan.GetProperty(role + "References"), runtimeReferences = plan.GetProperty(role + "RuntimeReferences"),
            resources, dependencies, frameworkInputs = plan.GetProperty("frameworkInputs"),
            frameworkProperties = plan.GetProperty("frameworkProperties"), frameworkGenerators = plan.GetProperty("frameworkGenerators"),
        });
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in plan.GetProperty(role + "Compile").EnumerateArray()) Add(file);
        foreach (var file in resources) Add(file);
        if (dependencies.ValueKind != JsonValueKind.Null)
            foreach (var file in dependencies.GetProperty("files").EnumerateArray()) Add(file);
        files.Add(DescriptorName, descriptor);

        GeneratedOutputPathSafety.EnsureSafeAncestors(root, includeLeaf: true, "role compilation cache");
        using var lease = GeneratedOutputLock.Acquire([root]);
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, MarkerName);
        if (!File.Exists(marker))
        {
            if (Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidDataException("Refusing an unmarked role compilation cache.");
            File.WriteAllBytes(marker, Marker);
        }
        GeneratedOutputPathSafety.EnsureSafeAncestors(marker, includeLeaf: true, "role compilation cache marker");
        if (!SunderRpcDescriptorFileReader.ReadStableBytes(marker, 1024, "role compilation cache marker").AsSpan().SequenceEqual(Marker))
            throw new InvalidDataException("The role compilation cache marker is invalid.");
        var output = Path.Combine(root, role + "-" + Convert.ToHexStringLower(SHA256.HashData(descriptor)));
        GeneratedOutputPathSafety.EnsureSafeAncestors(output, includeLeaf: true, "role compilation snapshot");
        if (!Directory.Exists(output))
        {
            var stage = output + ".stage-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(stage);
                foreach (var (relative, bytes) in files)
                {
                    var path = Path.Combine(stage, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, bytes);
                }
                Directory.Move(stage, output);
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
        }
        foreach (var (relative, bytes) in files)
        {
            var path = Path.Combine(output, relative);
            GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "role compilation input");
            if (!SunderRpcDescriptorFileReader.ReadStableBytes(path, 64 * 1024 * 1024, "role compilation input").AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("An immutable role compilation input changed after analysis.");
        }
        var actual = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(output);
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 32768) throw new InvalidDataException("The role compilation snapshot exceeds its file budget.");
                GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "role compilation membership");
                if (Directory.Exists(path)) pending.Push(path);
                else actual.Add(Path.GetRelativePath(output, path).Replace('\\', '/'));
            }
        }
        if (!actual.SetEquals(files.Keys)) throw new InvalidDataException("The immutable role compilation membership changed after analysis.");
        return output;

        void Add(JsonElement file)
        {
            var relative = file.GetProperty("path").GetString()!;
            if (!PackageStorageValidation.IsValidRelativePath(relative)) throw new InvalidDataException("A role compilation input path is invalid.");
            var path = Path.Combine(planRoot, relative);
            GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "role compilation input");
            var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, 64 * 1024 * 1024, "role compilation input");
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.GetProperty("sha256").GetString())
                throw new InvalidDataException("A role compilation input changed after analysis.");
            if (!files.TryAdd(relative, bytes) && !files[relative].AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("Conflicting role compilation input paths.");
        }
    }
}
