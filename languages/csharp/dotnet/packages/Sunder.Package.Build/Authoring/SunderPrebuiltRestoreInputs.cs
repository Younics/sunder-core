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
        if (root.GetProperty("version").GetInt32() != 3
            || root.TryGetProperty("logs", out var logs) && logs.EnumerateArray().Any(log => log.GetProperty("level").GetString() == "Error"))
            throw SunderPrebuiltRoleInputs.Invalid("A successful NuGet assets V3 graph is required.");
        var frameworks = root.GetProperty("project").GetProperty("frameworks");
        var selected = frameworks.EnumerateObject().SingleOrDefault(value => value.Name == framework
            || value.Value.TryGetProperty("targetAlias", out var alias) && alias.GetString() == framework);
        if (selected.Value.ValueKind != JsonValueKind.Object) throw SunderPrebuiltRoleInputs.Invalid("The restored framework is missing.");
        var targets = root.GetProperty("targets");
        var targetNames = new[] { selected.Name, selected.Name + "/" + rid };
        var projectedTargets = new JsonObject(); var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targetNames)
        {
            if (!targets.TryGetProperty(target, out var value)) throw SunderPrebuiltRoleInputs.Invalid("Restore must include the exact prebuilt framework/RID.");
            projectedTargets.Add(target, Portable(value));
            foreach (var library in value.EnumerateObject()) keys.Add(library.Name);
        }
        if (keys.Count > 8192) throw SunderPrebuiltRoleInputs.Invalid("The restored graph exceeds its library budget.");
        var libraries = root.GetProperty("libraries"); var projectedLibraries = new JsonObject();
        foreach (var key in keys.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var library = libraries.GetProperty(key);
            var kind = library.GetProperty("type").GetString();
            if (kind is not ("package" or "project")) throw SunderPrebuiltRoleInputs.Invalid("Unsupported restored library kind.");
            // Project source trees are captured separately. Project locators here
            // depend on the native host's checkout path and path separator.
            projectedLibraries.Add(key, Portable(library, omitProjectLocations: kind == "project"));
        }
        var projectedFramework = Portable(selected.Value)!.AsObject();
        if (projectedFramework.Remove("runtimeIdentifierGraphPath", out var graphPath))
        {
            var graph = SunderPrebuiltRoleInputs.File(Path.GetFullPath(graphPath!.GetValue<string>(), Path.GetDirectoryName(path)!),
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
