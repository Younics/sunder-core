using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Sdk.Storage;

namespace Sunder.Package.Build.Tasks;

public sealed partial class ReadSunderPackageRoleInputsTask
{
    [Output] public ITaskItem[] ResourceItems { get; private set; } = [];
    [Output] public ITaskItem[] ExcludedGenerators { get; private set; } = [];

    private ITaskItem[] ReadResources(JsonElement plan, string root)
    {
        if (!plan.TryGetProperty("resources", out var resources)) return [];
        var result = new List<ITaskItem>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadataByIdentity = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in resources.EnumerateArray())
        {
            if (known.Count >= 8192) throw new InvalidDataException("The role resource set exceeds its item budget.");
            var kind = resource.GetProperty("kind").GetString()!;
            var source = resource.GetProperty("sourcePath").GetString()!;
            var path = resource.GetProperty("path").GetString()!;
            var hash = resource.GetProperty("sha256").GetString()!;
            if (kind is not ("AvaloniaXaml" or "AvaloniaResource" or "EmbeddedResource" or "Content" or "None") || !Path.IsPathFullyQualified(source)
                || !PackageStorageValidation.IsValidRelativePath(path) || !path.StartsWith("resources/", StringComparison.Ordinal)
                || !known.Add(kind + ":" + source)) throw new InvalidDataException("A role resource has invalid identity or snapshot path.");
            var snapshot = Path.Combine(root, path);
            GeneratedOutputPathSafety.EnsureSafeAncestors(snapshot, includeLeaf: true, "role resource snapshot");
            Verify(source, hash); Verify(snapshot, hash);
            metadataByIdentity[kind + ":" + source] = resource.GetProperty("metadata");
            var roles = resource.GetProperty("roles").GetString();
            if (roles is not ("worker" or "app" or "shared")) throw new InvalidDataException("A role resource has invalid ownership.");
            if (roles != Role && roles != "shared") continue;
            required.Add(kind + ":" + source);
            var item = new TaskItem(snapshot); item.SetMetadata("ResourceKind", kind);
            foreach (var property in resource.GetProperty("metadata").EnumerateObject()) item.SetMetadata(property.Name, property.Value.GetString()!);
            result.Add(item);
        }
        if (ValidateResourceEvaluation)
        {
            var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in EvaluatedResources)
            {
                var kind = item.GetMetadata("ResourceKind");
                if (kind == "EmbeddedResource" && item.GetMetadata("LogicalName") == "!AvaloniaResources") continue;
                if (kind is "Content" or "None" && !Sunder.Package.Build.Authoring.SunderPackageResourcePlanner.Copies(item.GetMetadata("CopyToOutputDirectory"))
                    && !Sunder.Package.Build.Authoring.SunderPackageResourcePlanner.Copies(item.GetMetadata("CopyToPublishDirectory"))) continue;
                var key = kind + ":" + Path.GetFullPath(item.ItemSpec, ProjectDirectory);
                if (!known.Contains(key)) throw new InvalidDataException("An unplanned resource appeared during inner role evaluation.");
                var metadata = metadataByIdentity[key];
                foreach (var name in new[] { "SunderPackageAssetRole", "SunderPackageResourceOwner", "CopyToOutputDirectory", "CopyToPublishDirectory" })
                {
                    var expected = metadata.TryGetProperty(name, out var value) ? value.GetString() : string.Empty;
                    if (item.GetMetadata(name) != expected) throw new InvalidDataException("Resource role/copy metadata changed during inner evaluation.");
                }
                var link = item.GetMetadata("Link");
                if (link.Length == 0) link = Path.GetRelativePath(ProjectDirectory, Path.GetFullPath(item.ItemSpec, ProjectDirectory)).Replace('\\', '/');
                if (link.Replace('\\', '/') != metadata.GetProperty("Link").GetString()) throw new InvalidDataException("A resource Link identity changed during inner evaluation.");
                var target = item.GetMetadata("TargetPath");
                if (target.Length != 0 && target.Replace('\\', '/') != (metadata.TryGetProperty("TargetPath", out var expectedTarget) ? expectedTarget.GetString()!.Replace('\\', '/') : link))
                    throw new InvalidDataException("A resource TargetPath changed during inner evaluation.");
                actual.Add(key);
            }
            if (!required.IsSubsetOf(actual)) throw new InvalidDataException("A selected resource disappeared during inner role evaluation.");
            if (Role == "app" && plan.TryGetProperty("frameworkProperties", out var properties))
            {
                var evaluated = EvaluatedGeneratorProperties.ToDictionary(property => property.ItemSpec, property => property.GetMetadata("Value"), StringComparer.Ordinal);
                foreach (var property in properties.EnumerateObject())
                    if (!evaluated.TryGetValue(property.Name, out var value) || value != property.Value.GetString())
                        throw new InvalidDataException("Avalonia generation/compilation settings changed during inner evaluation.");
            }
        }
        if (plan.TryGetProperty("frameworkInputs", out var inputs))
            foreach (var input in inputs.EnumerateArray()) Verify(input.GetProperty("path").GetString()!, input.GetProperty("sha256").GetString()!);
        return result.ToArray();

        static void Verify(string path, string expected)
        {
            var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, 64 * 1024 * 1024, "role resource input");
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expected) throw new InvalidDataException("A resource or framework generator changed after role analysis.");
        }
    }
}
