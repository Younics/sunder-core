using System.Text.Json;

namespace Sunder.Package.Build.Authoring;

/// <summary>The shared NuGet V3/V4 selection contract; callers retain their own evidence and projection policies.</summary>
internal sealed class SunderNuGetAssets
{
    private readonly JsonElement _root;
    private readonly Func<string, InvalidDataException> _invalid;
    internal int Format { get; }

    internal SunderNuGetAssets(JsonElement root, Func<string, InvalidDataException> invalid)
    {
        _root = root; _invalid = invalid;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var format) || format is not (3 or 4))
            throw invalid("A successful NuGet assets V3 or V4 graph is required; other formats are unsupported.");
        Format = format;
        if (!root.TryGetProperty("logs", out var logs)) return;
        if (logs.ValueKind != JsonValueKind.Array) throw invalid("Restore logs must be an array.");
        foreach (var log in logs.EnumerateArray())
        {
            var level = Text(log, "level");
            if (level == "Error") throw invalid("The restored dependency graph contains errors.");
            if (level is not ("Debug" or "Verbose" or "Information" or "Minimal" or "Warning")) throw invalid("Unsupported restore log level.");
        }
    }

    internal Selection SelectFramework(string requested)
    {
        Selection? selected = null;
        foreach (var entry in Object(Object(_root, "project"), "frameworks").EnumerateObject())
        {
            var candidate = Framework(entry);
            // V4 aliases can intentionally resolve different libraries for one
            // effective TFM. Never substitute a nearest or first equivalent TFM.
            if (entry.Name != requested && (Format != 3 || candidate.Alias != requested)) continue;
            if (selected is not null) throw _invalid("The restored framework selection is ambiguous.");
            selected = candidate;
        }
        return selected ?? throw _invalid("The exact restored framework/alias is missing.");
    }

    internal void ValidateRestoreFramework(Selection selected)
    {
        var frameworks = Object(Object(Object(_root, "project"), "restore"), "frameworks");
        var entry = frameworks.EnumerateObject().SingleOrDefault(entry => entry.Name == selected.Key);
        if (entry.Value.ValueKind != JsonValueKind.Object) throw _invalid("The selected restore framework is missing.");
        var restored = Framework(entry);
        if (restored.EffectiveFramework != selected.EffectiveFramework
            || entry.Value.TryGetProperty("targetAlias", out _) && restored.Alias != selected.Alias)
            throw _invalid("Restore metadata disagrees with the selected framework/alias.");
    }

    private Selection Framework(JsonProperty entry)
    {
        if (entry.Value.ValueKind != JsonValueKind.Object) throw _invalid("Restored framework entries must be objects.");
        var alias = entry.Value.TryGetProperty("targetAlias", out _) ? Text(entry.Value, "targetAlias") : null;
        var declared = entry.Value.TryGetProperty("framework", out _) ? Text(entry.Value, "framework") : null;
        if (Format == 4 && (declared is null || entry.Name != (alias ?? declared))
            || Format == 3 && declared is not null && declared != entry.Name)
            throw _invalid("The restored framework key, alias and effective framework disagree with the assets format.");
        return new(entry.Name, Format == 4 ? declared! : entry.Name, alias ?? entry.Name, entry.Value);
    }

    internal JsonElement Object(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : throw _invalid($"Restored '{name}' must be an object.");

    internal string Text(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text && text.Trim() == text
            ? text : throw _invalid($"Restored '{name}' must be a nonempty string.");

    internal string LibraryKind(JsonElement library)
    {
        var kind = Text(library, "type");
        return kind is "package" or "project" ? kind : throw _invalid("Unsupported restored library kind.");
    }

    internal void ValidateTargetLibrary(JsonElement library)
    {
        if (library.TryGetProperty("dependencies", out var dependencies)
            && (dependencies.ValueKind != JsonValueKind.Object || dependencies.EnumerateObject().Any(dependency => dependency.Value.ValueKind != JsonValueKind.String)))
            throw _invalid("Restored target dependencies must be a string-valued object.");
        foreach (var group in new[] { "compile", "runtime", "native", "resource", "runtimeTargets", "build", "buildMultiTargeting", "contentFiles", "tools", "embed", "analyzers" })
            if (library.TryGetProperty(group, out var assets)
                && (assets.ValueKind != JsonValueKind.Object || assets.EnumerateObject().Any(asset => asset.Value.ValueKind != JsonValueKind.Object)))
                throw _invalid($"Restored '{group}' assets must be an object-valued map.");
    }

    internal sealed record Selection(string Key, string EffectiveFramework, string Alias, JsonElement Metadata);
}
