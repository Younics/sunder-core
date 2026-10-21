using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Authoring;

internal static class SunderPrebuiltRestoreInputs
{
    // Compare resolved dependency selection without importing native checkout,
    // cache, generated-output or restore-source locations as portable authority.
    internal static SunderPrebuiltFile Capture(string path, string framework, string rid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, 64 * 1024 * 1024, "restored prebuilt dependency graph");
        using var document = SunderRpcAuthoringJson.ParseRegistryMetadata(bytes, 64 * 1024 * 1024);
        var root = document.RootElement;
        var assets = new SunderNuGetAssets(root, SunderPrebuiltRoleInputs.Invalid);
        var selected = assets.SelectFramework(framework);
        var targets = assets.Object(root, "targets");
        // NuGet V4 pivots targets/project.frameworks on the evaluated alias, with
        // the effective TFM recorded in framework. V3 pivots on the effective TFM.
        // Normalize only that representation difference; keep targetAlias and all
        // selected library/asset metadata bound, including unknown additive fields.
        var targetNames = new[] { (Source: selected.Key, Portable: selected.EffectiveFramework),
            (Source: selected.Key + "/" + rid, Portable: selected.EffectiveFramework + "/" + rid) };
        var projectedTargets = new JsonObject(); var keys = new HashSet<string>(StringComparer.Ordinal);
        var libraries = assets.Object(root, "libraries");
        foreach (var target in targetNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!targets.TryGetProperty(target.Source, out var value) || value.ValueKind != JsonValueKind.Object)
                throw SunderPrebuiltRoleInputs.Invalid("Restore must include the exact prebuilt framework/RID as object-valued targets.");
            foreach (var library in value.EnumerateObject())
            {
                var kind = assets.LibraryKind(library.Value);
                if (!libraries.TryGetProperty(library.Name, out var restored) || assets.LibraryKind(restored) != kind)
                    throw SunderPrebuiltRoleInputs.Invalid("A selected target library is missing or disagrees with the restored library kind.");
                assets.ValidateTargetLibrary(library.Value);
                keys.Add(library.Name);
                if (keys.Count > 8192) throw SunderPrebuiltRoleInputs.Invalid("The restored graph exceeds its library budget.");
            }
            projectedTargets.Add(target.Portable, Portable(value));
        }
        var projectedLibraries = new JsonObject();
        foreach (var key in keys.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var library = libraries.GetProperty(key);
            var kind = assets.LibraryKind(library);
            if (kind == "package") { _ = assets.Text(library, "sha512"); _ = assets.Text(library, "path"); }
            if (library.TryGetProperty("files", out var files)
                && (files.ValueKind != JsonValueKind.Array || files.EnumerateArray().Any(file => file.ValueKind != JsonValueKind.String)))
                throw SunderPrebuiltRoleInputs.Invalid("Restored library files must be a string array.");
            // Project source trees are captured separately. Project locators here
            // depend on the native host's checkout path and path separator.
            projectedLibraries.Add(key, Portable(library, omitProjectLocations: kind == "project"));
        }
        var projectedFramework = Portable(selected.Metadata)!.AsObject();
        // The effective framework is already encoded in the normalized target keys.
        projectedFramework.Remove("framework");
        if (projectedFramework.Remove("runtimeIdentifierGraphPath"))
        {
            var graph = SunderPrebuiltRoleInputs.File(Path.GetFullPath(assets.Text(selected.Metadata, "runtimeIdentifierGraphPath"), Path.GetDirectoryName(path)!),
                "runtimeIdentifierGraph", 16 * 1024 * 1024, cancellationToken);
            projectedFramework["runtimeIdentifierGraphSha256"] = graph.Sha256;
        }
        var projection = new JsonObject
        {
            ["targets"] = projectedTargets, ["libraries"] = projectedLibraries,
            ["framework"] = projectedFramework,
        };
        var canonical = SunderPrebuiltRoleInputs.Write(projection);
        return new("restore:graph", canonical.Length, SunderPrebuiltRoleInputs.Hash(canonical));

        static JsonNode? Portable(JsonElement value, bool omitProjectLocations = false) => value.ValueKind switch
        {
            JsonValueKind.Object => new JsonObject(value.EnumerateObject()
                .Where(property => !omitProjectLocations || property.Name is not ("path" or "msbuildProject"))
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => new KeyValuePair<string, JsonNode?>(property.Name.Replace('\\', '/'), Portable(property.Value)))),
            JsonValueKind.Array => new JsonArray(value.EnumerateArray().Select(item => Portable(item)).ToArray()),
            JsonValueKind.String => JsonValue.Create(value.GetString()!.Replace('\\', '/')),
            _ => JsonNode.Parse(value.GetRawText()),
        };
    }

}
