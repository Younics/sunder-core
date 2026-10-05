using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Storage;

namespace Sunder.Package.Build.Authoring;

internal static partial class SunderPackageResourcePlanner
{
    private static readonly string[] MetadataNames = ["Link", "LogicalName", "ManifestResourceName", "TargetPath", "CopyToOutputDirectory",
        "CopyToPublishDirectory", "DependentUpon", "WithCulture", "Culture", "Type", "SunderPackageAssetRole", "SunderPackageResourceOwner"];

    internal static IReadOnlyList<SunderPackageResourceInput> Read(string projectDirectory, params (string Kind, ITaskItem[] Items)[] groups)
    {
        var result = new List<SunderPackageResourceInput>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var (kind, items) in groups)
        {
            if (kind is not ("AvaloniaXaml" or "AvaloniaResource" or "EmbeddedResource" or "Content" or "None"))
                throw Invalid("resource-kind-invalid", $"Unsupported resource item kind '{kind}'.");
            foreach (var item in items)
            {
                if (kind == "EmbeddedResource" && item.GetMetadata("LogicalName") == "!AvaloniaResources") continue;
                if (kind is "None" or "Content" && !Copies(item.GetMetadata("CopyToOutputDirectory")) && !Copies(item.GetMetadata("CopyToPublishDirectory"))) continue;
                var path = Path.GetFullPath(item.ItemSpec, projectDirectory);
                if (!seen.Add(kind + ":" + path)) throw Invalid("resource-duplicate", $"Resource '{path}' repeats item kind '{kind}'.");
                if (result.Count == 8192) throw Invalid("resource-limit-exceeded", "The resource item budget was exceeded.");
                var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, 64 * 1024 * 1024, "authored package resource");
                if ((total += bytes.Length) > 256L * 1024 * 1024) throw Invalid("resource-limit-exceeded", "The resource byte budget was exceeded.");
                var metadata = MetadataNames.Where(name => item.GetMetadata(name).Length != 0).ToDictionary(name => name, item.GetMetadata, StringComparer.Ordinal);
                if (!metadata.TryGetValue("Link", out var logical)) logical = Path.GetRelativePath(projectDirectory, path).Replace('\\', '/');
                logical = logical.Replace('\\', '/');
                if (!PackageStorageValidation.IsValidRelativePath(logical)) throw Invalid("resource-path-invalid", $"Linked resource '{path}' requires a portable Link path.");
                metadata["Link"] = logical;
                if (metadata.TryGetValue("TargetPath", out var target) && !PackageStorageValidation.IsValidRelativePath(target.Replace('\\', '/')))
                    throw Invalid("resource-path-invalid", $"Resource '{path}' has an unsafe TargetPath.");
                if (kind == "AvaloniaXaml" || kind == "AvaloniaResource" && Path.GetExtension(path).ToLowerInvariant() is ".axaml" or ".xaml" or ".paml")
                    _ = Xml(bytes, path);
                if (Path.GetExtension(path).Equals(".resx", StringComparison.OrdinalIgnoreCase))
                {
                    var xml = Xml(bytes, path);
                    if (xml.Descendants().Any(element => element.Name.LocalName == "data" && element.Attribute("type")?.Value.Contains("ResXFileRef", StringComparison.Ordinal) == true))
                        throw Invalid("resource-reference-unsupported", $"Resource '{path}' has external ResXFileRef input; declare its closed resource dependency before role projection.");
                }
                result.Add(new(kind, path, bytes, metadata));
            }
        }
        return result.OrderBy(resource => resource.Kind, StringComparer.Ordinal).ThenBy(resource => resource.SourcePath, StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<SunderPackageResourceEntry> Plan(SunderPackageAuthoringAnalysis analysis, IReadOnlyList<SunderPackageResourceInput> inputs, string rootNamespace)
    {
        var types = analysis.RolePlan.Types.ToDictionary(type => type.MetadataName, StringComparer.Ordinal);
        var uses = FindResourceUses(analysis, types);
        var result = new List<SunderPackageResourceEntry>();
        var logicalOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            var metadata = input.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var role = SunderPackageRoleMembership.None;
            var intrinsic = input.Kind is "AvaloniaXaml" or "AvaloniaResource";
            if (intrinsic) role = SunderPackageRoleMembership.App;
            var logical = metadata.GetValueOrDefault("LogicalName") ?? metadata.GetValueOrDefault("ManifestResourceName");
            if (input.Kind == "EmbeddedResource")
            {
                logical ??= (rootNamespace.Length == 0 ? "" : rootNamespace + ".") + metadata["Link"].Replace('/', '.').Replace('\\', '.');
                if (logical.EndsWith(".resx", StringComparison.OrdinalIgnoreCase)) logical = logical[..^5] + ".resources";
                metadata["LogicalName"] = logical;
                if (uses.TryGetValue(logical, out var used)) role |= used;
            }
            if (metadata.TryGetValue("SunderPackageResourceOwner", out var owner))
            {
                if (!types.TryGetValue(owner.StartsWith("global::", StringComparison.Ordinal) ? owner : "global::" + owner, out var type))
                    throw Invalid("resource-owner-invalid", $"Resource '{input.SourcePath}' names unknown source owner '{owner}'.");
                role |= type.Roles;
            }
            if (metadata.TryGetValue("SunderPackageAssetRole", out var declared))
            {
                var requested = declared switch { "worker" => SunderPackageRoleMembership.Worker, "app" => SunderPackageRoleMembership.App,
                    "shared" => SunderPackageRoleMembership.Shared, _ => throw Invalid("resource-role-invalid", $"Resource '{input.SourcePath}' has invalid role '{declared}'.") };
                if ((role & ~requested) != 0 || intrinsic && requested != SunderPackageRoleMembership.App)
                    throw Invalid("resource-role-conflict", $"Resource '{input.SourcePath}' has incompatible declared and inferred roles.");
                role = requested;
            }
            if (role == SunderPackageRoleMembership.None)
                throw Invalid("resource-role-ambiguous", $"Resource '{input.SourcePath}' needs an inferred literal use, SunderPackageResourceOwner, or SunderPackageAssetRole.");
            if (!logicalOwners.Add(input.Kind + ":" + (logical ?? metadata["Link"])))
                throw Invalid("resource-name-collision", $"Resource '{input.SourcePath}' repeats a logical resource identity.");
            var hash = Convert.ToHexStringLower(SHA256.HashData(input.Contents));
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.Kind + ":" + input.SourcePath)));
            result.Add(new(input.Kind, input.SourcePath, "resources/" + key + "/" + Path.GetFileName(input.SourcePath), hash, role, metadata));
        }
        return result;
    }

    private static Dictionary<string, SunderPackageRoleMembership> FindResourceUses(SunderPackageAuthoringAnalysis analysis, IReadOnlyDictionary<string, SunderPackageRoleType> types)
    {
        var result = new Dictionary<string, SunderPackageRoleMembership>(StringComparer.Ordinal);
        var compilation = analysis.Compilation.SourceCompilation!;
        foreach (var tree in compilation.SyntaxTrees.Where(tree => analysis.OrdinarySourcePaths.Contains(tree.FilePath)))
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method || method.Name != "GetManifestResourceStream"
                    || method.ContainingType.ToDisplayString() != "System.Reflection.Assembly" || invocation.ArgumentList.Arguments.Count != 1) continue;
                if (model.GetConstantValue(invocation.ArgumentList.Arguments[0].Expression) is not { HasValue: true, Value: string name }) continue;
                var owner = model.GetEnclosingSymbol(invocation.SpanStart)?.ContainingType;
                if (owner is null || !types.TryGetValue(SunderPackageDependencyDiscovery.Name(SunderPackageDependencyDiscovery.Owner(owner)), out var type)) continue;
                result.TryGetValue(name, out var role); result[name] = role | type.Roles;
            }
        }
        return result;
    }

    internal static XDocument Xml(byte[] bytes, string path)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
            return XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        }
        catch (XmlException error) { throw Invalid("resource-xml-invalid", $"{path}({error.LineNumber},{error.LinePosition}): {error.Message}"); }
    }
    internal static bool Copies(string value) => value.Length != 0 && !value.Equals("Never", StringComparison.OrdinalIgnoreCase);
    private static InvalidDataException Invalid(string code, string message) => new("package-authoring." + code + ": " + message);
}
