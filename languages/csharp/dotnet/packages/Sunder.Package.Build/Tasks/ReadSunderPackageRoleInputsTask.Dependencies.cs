using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Sdk.Storage;

namespace Sunder.Package.Build.Tasks;

public sealed partial class ReadSunderPackageRoleInputsTask
{
    [Output] public string ProjectAssetsFile { get; private set; } = string.Empty;
    [Output] public string ProjectExtensionsPath { get; private set; } = string.Empty;
    [Output] public ITaskItem[] CompileAssetPaths { get; private set; } = [];
    [Output] public ITaskItem[] RuntimeAssetPaths { get; private set; } = [];
    [Output] public ITaskItem[] ExcludedProjectReferences { get; private set; } = [];

    private DependencyInputs? ReadDependencies(JsonElement plan, string root)
    {
        if (!plan.TryGetProperty(Role + "Dependencies", out var dependencies) || dependencies.ValueKind == JsonValueKind.Null) return null;
        var prefix = "roles/" + Role + "/restore/";
        if (dependencies.GetProperty("projectAssetsFile").GetString() != prefix + "project.assets.json"
            || dependencies.GetProperty("projectExtensionsPath").GetString() != prefix)
            throw new InvalidDataException("The role restore input paths do not match the selected role.");
        var inputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in dependencies.GetProperty("inputs").EnumerateArray())
        {
            if (inputs.Count >= 65536) throw new InvalidDataException("The role dependency input count exceeds its budget.");
            var path = input.GetProperty("path").GetString()!;
            if (!Path.IsPathFullyQualified(path) || !inputs.Add(path)) throw new InvalidDataException("A role dependency input path is invalid or duplicated.");
            Verify(path, input.GetProperty("sha256").GetString());
        }
        foreach (var file in dependencies.GetProperty("files").EnumerateArray())
        {
            var relative = file.GetProperty("path").GetString()!;
            if (!PackageStorageValidation.IsValidRelativePath(relative) || !relative.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("A generated restore input escapes its selected role.");
            var path = Path.Combine(root, relative);
            GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "role restore input");
            Verify(path, file.GetProperty("sha256").GetString());
        }
        return new(Path.Combine(root, prefix, "project.assets.json"), Path.Combine(root, prefix),
            Assets("compilePaths"), Assets("runtimePaths"), dependencies.GetProperty("excludedProjects").EnumerateArray()
                .Select(value => (ITaskItem)new TaskItem(Absolute(value.GetString()))).ToArray());

        ITaskItem[] Assets(string name) => dependencies.GetProperty(name).EnumerateArray().Select(value =>
        {
            var path = Absolute(value.GetString());
            if (!inputs.Contains(path)) throw new InvalidDataException("A selected role asset has no verified input evidence.");
            return (ITaskItem)new TaskItem(path);
        }).ToArray();
        static string Absolute(string? path) => path is not null && Path.IsPathFullyQualified(path) ? path
            : throw new InvalidDataException("A role dependency path must be absolute.");
        static void Verify(string path, string? hash)
        {
            var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, 268_435_456, "package role dependency input");
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash) throw new InvalidDataException("A package dependency input changed after role analysis.");
        }
    }

    private sealed record DependencyInputs(string AssetsFile, string ExtensionsPath, ITaskItem[] Compile, ITaskItem[] Runtime, ITaskItem[] ExcludedProjects);
}
