using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Build.Framework;
using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Storage;

namespace Sunder.Package.Build.Authoring;

/// <summary>Projects an already resolved NuGet graph. It never resolves versions or performs restore/network work.</summary>
internal sealed partial class SunderPackageDependencyPlanner
{
    private readonly string _project;
    private readonly string _directory;
    private readonly string _extensions;
    private readonly JsonObject _assets;
    private readonly JsonObject _libraries;
    private readonly string _framework;
    private readonly string _runtimeTarget;
    private readonly Dictionary<string, string> _keys;
    private readonly Dictionary<string, SunderPackageRoleMembership> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SunderPackageRoleMembership> _constraints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _projectKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _packagePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SunderPackageFileEvidence> _inputs = new(StringComparer.Ordinal);
    private readonly ITaskItem[] _projectReferences;
    private readonly ITaskItem[] _compileReferences;
    private readonly ITaskItem[] _runtimeReferences;
    private readonly List<string> _roots = [];

    internal SunderPackageDependencyPlanner(string assetsPath, string project, string extensions, string framework, string rid,
        ITaskItem[] packageReferences, ITaskItem[] projectReferences, ITaskItem[] imports, ITaskItem[]? compileReferences = null, ITaskItem[]? runtimeReferences = null)
    {
        _project = Path.GetFullPath(project); _directory = Path.GetDirectoryName(_project)!;
        _extensions = Path.GetFullPath(extensions, _directory); _projectReferences = projectReferences;
        _compileReferences = compileReferences ?? []; _runtimeReferences = runtimeReferences ?? [];
        _assets = JsonNode.Parse(Read(assetsPath)) as JsonObject ?? throw Invalid("The restored assets file must be an object.");
        if (_assets["version"]?.GetValue<int>() != 3) throw Invalid("Only NuGet assets format 3 is supported.");
        if ((_assets["logs"] as JsonArray)?.Any(log => log?["level"]?.GetValue<string>() == "Error") == true) throw Invalid("The restored graph contains errors.");
        _libraries = Object(_assets, "libraries");
        if (_libraries.Count > 8192) throw Invalid("The restored dependency graph exceeds its library budget.");
        var frameworks = Object(Object(_assets, "project"), "frameworks");
        _framework = frameworks.SingleOrDefault(pair => pair.Key == framework || pair.Value?["targetAlias"]?.GetValue<string>() == framework).Key
            ?? throw Invalid("The restored graph does not contain the evaluated target framework.");
        _runtimeTarget = _framework + "/" + rid;
        var targets = Object(_assets, "targets");
        if (targets[_framework] is not JsonObject || targets[_runtimeTarget] is not JsonObject)
            throw Invalid($"Restore must include the selected framework/RID '{_runtimeTarget}'.");
        _keys = Object(targets, _runtimeTarget).ToDictionary(pair => Id(pair.Key), pair => pair.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in _libraries)
        {
            if (value?["type"]?.GetValue<string>() == "project")
            {
                var path = value["msbuildProject"]?.GetValue<string>() ?? value["path"]!.GetValue<string>();
                _projectKeys.Add(Path.GetFullPath(path, _directory), key);
            }
            else if (value?["type"]?.GetValue<string>() == "package")
            {
                var relative = value["path"]!.GetValue<string>();
                if (!PackageStorageValidation.IsValidRelativePath(relative)) throw Invalid("A package cache path is not portable.");
                var matches = Object(_assets, "packageFolders").Select(folder => Path.GetFullPath(Path.Combine(folder.Key, relative)))
                    .Where(Directory.Exists).ToArray();
                if (matches.Length == 0) throw Invalid($"Restored package '{key}' is absent from the package cache.");
                _packagePaths.Add(key, matches[0]);
            }
            else throw Invalid($"Unsupported restored library kind in '{key}'.");
        }
        foreach (var item in packageReferences)
        {
            if (!_keys.TryGetValue(item.ItemSpec, out var key))
            {
                // Download-only/framework references are not PackageReference roots.
                if (item.GetMetadata("IsImplicitlyDefined").Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
                throw Invalid($"PackageReference '{item.ItemSpec}' is absent from the selected restored graph.");
            }
            _roots.Add(key); Constrain(key, item.GetMetadata("SunderPackageAssetRole"));
        }
        foreach (var item in projectReferences)
        {
            var path = Path.GetFullPath(item.ItemSpec, _directory);
            if (!_projectKeys.TryGetValue(path, out var key))
            {
                if (item.GetMetadata("ReferenceOutputAssembly").Equals("false", StringComparison.OrdinalIgnoreCase)) continue;
                throw Invalid($"ProjectReference '{item.ItemSpec}' is absent from the selected restored graph.");
            }
            _roots.Add(key); Constrain(key, item.GetMetadata("SunderPackageAssetRole"));
        }
        foreach (var key in _keys.Values)
        {
            var id = Id(key);
            if (id == "Sunder.Package.Build") Add(key, SunderPackageRoleMembership.Shared);
            if (id == "Sunder.Sdk.Worker") Constrain(key, "worker");
            if (id == "Sunder.Sdk.Avalonia" || id == "Avalonia" || id.StartsWith("Avalonia.", StringComparison.Ordinal)) Constrain(key, "app");
        }
        _ = Read(_project);
        foreach (var item in imports.Where(item => !string.IsNullOrWhiteSpace(item.ItemSpec))) _ = Read(Path.GetFullPath(item.ItemSpec, _directory));
        // The exact generated import files are part of the handoff, not new restore authority.
        foreach (var suffix in new[] { ".nuget.g.props", ".nuget.g.targets" }) _ = Read(Path.Combine(_extensions, Path.GetFileName(_project) + suffix));
    }

    internal void UseAssemblies(SunderPackageRoleMembership role, IEnumerable<string> names, ITaskItem[] references)
    {
        var target = Object(Object(_assets, "targets"), _runtimeTarget);
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var metadataOwners = references.Where(item => Path.GetFileNameWithoutExtension(item.ItemSpec) == name)
                .Select(item => item.GetMetadata("NuGetPackageId"))
                .Where(id => _keys.ContainsKey(id)).Select(id => _keys[id]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var owners = metadataOwners.Length == 1 ? metadataOwners : target.Where(pair => new[] { "compile", "runtime" }
                .Any(group => (pair.Value?[group] as JsonObject)?.Any(asset => Path.GetFileNameWithoutExtension(asset.Key) == name) == true))
                .Select(pair => pair.Key).ToArray();
            if (owners.Length > 1) throw Invalid($"Assembly '{name}' has ambiguous restored-library ownership.");
            if (owners.Length == 1) Add(owners[0], role);
        }
    }

    internal void Complete()
    {
        foreach (var (key, role) in _constraints) Add(key, role);
        foreach (var key in _roots.Distinct(StringComparer.OrdinalIgnoreCase))
            if (!_owners.ContainsKey(key)) throw Invalid($"Dependency '{key}' has no inferable role. Declare SunderPackageAssetRole=worker, app or shared on its reference.", "dependency-role-ambiguous");
        foreach (var (key, role) in _owners)
            if (_constraints.TryGetValue(key, out var allowed) && (role & ~allowed) != 0)
                throw Invalid($"Dependency '{key}' is required by {role}, but its declared role is {allowed}.", "dependency-role-conflict");
    }

    internal SunderPackageDependencyPlan Project(SunderPackageRoleMembership role)
    {
        var selected = _owners.Where(pair => (pair.Value & role) != 0).Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var document = (JsonObject)_assets.DeepClone();
        var targets = Object(document, "targets");
        foreach (var target in targets.ToArray())
        {
            if (target.Key != _framework && target.Key != _runtimeTarget) { targets.Remove(target.Key); continue; }
            var entries = target.Value as JsonObject ?? throw Invalid("A restored target must be an object.");
            foreach (var entry in entries.ToArray()) if (!selected.Contains(entry.Key)) entries.Remove(entry.Key);
        }
        foreach (var library in Object(document, "libraries").ToArray()) if (!selected.Contains(library.Key)) Object(document, "libraries").Remove(library.Key);
        var selectedIds = selected.Select(Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Object(document, "projectFileDependencyGroups").ToArray())
            if (group.Key != _framework) Object(document, "projectFileDependencyGroups").Remove(group.Key);
            else Object(document, "projectFileDependencyGroups")[group.Key] = new JsonArray(((JsonArray)group.Value!).Select(value => value!.GetValue<string>())
                .Where(value => selectedIds.Contains(value.Split(' ')[0])).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        var project = Object(document, "project");
        var frameworks = Object(project, "frameworks");
        foreach (var entry in frameworks.ToArray())
            if (entry.Key != _framework) frameworks.Remove(entry.Key);
            else if (entry.Value?["dependencies"] is JsonObject dependencies)
                foreach (var dependency in dependencies.ToArray()) if (!selectedIds.Contains(dependency.Key)) dependencies.Remove(dependency.Key);
        var restore = Object(project, "restore");
        foreach (var entry in Object(restore, "frameworks").ToArray())
        {
            if (entry.Key != _framework) { Object(restore, "frameworks").Remove(entry.Key); continue; }
            if (entry.Value?["projectReferences"] is JsonObject projects)
                foreach (var reference in projects.ToArray())
                    if (!_projectKeys.TryGetValue(Path.GetFullPath(reference.Key, _directory), out var key) || !selected.Contains(key)) projects.Remove(reference.Key);
        }
        var compile = new HashSet<string>(StringComparer.Ordinal);
        var runtime = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in selected.Order(StringComparer.Ordinal))
        {
            var target = Object(Object(_assets, "targets"), _runtimeTarget)[key];
            if (!_packagePaths.TryGetValue(key, out var directory))
            {
                var projectPath = _projectKeys.Single(pair => pair.Value == key).Key;
                _ = Read(projectPath);
                ProjectAssets("compile", _compileReferences, compile);
                ProjectAssets("runtime", _runtimeReferences, runtime);
                continue;
            }
            foreach (var file in (JsonArray)_libraries[key]!["files"]!)
            {
                var path = AssetPath(directory, file!.GetValue<string>());
                _ = Read(path);
            }
            foreach (var group in new[] { "compile", "runtime", "native", "resource", "runtimeTargets" })
                foreach (var asset in target?[group] as JsonObject ?? [])
                {
                    if (Path.GetFileName(asset.Key) == "_._") continue;
                    var path = AssetPath(directory, asset.Key);
                    (group == "compile" ? compile : runtime).Add(path);
                }

            void ProjectAssets(string group, ITaskItem[] evaluated, HashSet<string> output)
            {
                var names = (target?[group] as JsonObject ?? []).Select(asset => Path.GetFileNameWithoutExtension(asset.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var item in evaluated.Where(item => names.Contains(Path.GetFileNameWithoutExtension(item.ItemSpec))))
                {
                    var path = Path.GetFullPath(item.ItemSpec, _directory);
                    _ = Read(path); output.Add(path);
                }
            }
        }
        var files = new List<SunderPackageDependencyFile> { new("project.assets.json", JsonSerializer.SerializeToUtf8Bytes(document)) };
        foreach (var suffix in new[] { ".nuget.g.props", ".nuget.g.targets" })
            files.Add(new(Path.GetFileName(_project) + suffix, ProjectImports(suffix, selected)));
        return new(selected.Order(StringComparer.Ordinal).ToArray(), _projectReferences.Select(item => Path.GetFullPath(item.ItemSpec, _directory))
                .Where(path => _projectKeys.TryGetValue(path, out var key) && !selected.Contains(key)).Order(StringComparer.Ordinal).ToArray(),
            compile.Order(StringComparer.Ordinal).ToArray(), runtime.Order(StringComparer.Ordinal).ToArray(),
            _inputs.Values.OrderBy(input => input.Path, StringComparer.Ordinal).ToArray(), files);
    }

    internal void VerifyInputs()
    {
        foreach (var input in _inputs.Values)
            if (Hash(SunderRpcDescriptorFileReader.ReadStableBytes(input.Path, int.MaxValue, "package dependency input")) != input.Sha256)
                throw Invalid("A dependency input changed during role planning.");
    }

    private void Add(string key, SunderPackageRoleMembership role)
    {
        var pending = new Queue<string>(); pending.Enqueue(key);
        while (pending.TryDequeue(out var current))
        {
            _owners.TryGetValue(current, out var previous);
            if ((previous | role) == previous) continue;
            _owners[current] = previous | role;
            foreach (var targetName in new[] { _framework, _runtimeTarget })
                foreach (var dependency in Object(Object(_assets, "targets"), targetName)[current]?["dependencies"] as JsonObject ?? [])
                {
                    if (!_keys.TryGetValue(dependency.Key, out var dependencyKey)) throw Invalid($"Restored dependency '{dependency.Key}' is missing.");
                    pending.Enqueue(dependencyKey);
                }
        }
    }

    private void Constrain(string key, string value)
    {
        if (value.Length == 0) return;
        var role = value switch { "worker" => SunderPackageRoleMembership.Worker, "app" => SunderPackageRoleMembership.App,
            "shared" => SunderPackageRoleMembership.Shared, _ => throw Invalid($"Invalid SunderPackageAssetRole '{value}'.") };
        if (_constraints.TryGetValue(key, out var previous) && previous != role) throw Invalid($"Dependency '{key}' has conflicting role constraints.", "dependency-role-conflict");
        _constraints[key] = role;
    }

    private byte[] Read(string path)
    {
        path = Path.GetFullPath(path, _directory);
        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, 268_435_456, "restored package input");
        var evidence = new SunderPackageFileEvidence(path, Hash(bytes));
        if (_inputs.TryGetValue(path, out var previous) && previous != evidence) throw Invalid("A restored package input changed during analysis.");
        if (_inputs.Count >= 65536 && !_inputs.ContainsKey(path)) throw Invalid("The restored dependency input set exceeds its file budget.");
        _inputs[path] = evidence; return bytes;
    }
    private static string AssetPath(string directory, string relative)
        => PackageStorageValidation.IsValidRelativePath(relative) ? Path.GetFullPath(Path.Combine(directory, relative)) : throw Invalid("A restored asset path is not portable.");
    private static string Id(string key) => key[..key.LastIndexOf('/')];
    private static JsonObject Object(JsonObject value, string name) => value[name] as JsonObject ?? throw Invalid($"Restored graph is missing '{name}'.");
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static InvalidDataException Invalid(string message, string code = "dependency-plan-invalid") => new("package-authoring." + code + ": " + message);
}
