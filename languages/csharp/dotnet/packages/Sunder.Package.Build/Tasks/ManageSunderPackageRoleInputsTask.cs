using System.Text;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Package.Build.Authoring;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Format;
using static Sunder.Package.Build.Authoring.SunderPrebuiltRoleInputs;

namespace Sunder.Package.Build.Tasks;

/// <summary>Captures, exports and validates portable exact-RID generated-role inputs.</summary>
public sealed partial class ManageSunderPackageRoleInputsTask : Microsoft.Build.Utilities.Task, ICancelableTask
{
    private readonly CancellationTokenSource _cancellation = new();
    [Required] public string Action { get; set; } = string.Empty;
    [Required] public string ProjectDirectory { get; set; } = string.Empty;
    public string ProjectFile { get; set; } = string.Empty;
    public string ProjectAssetsFile { get; set; } = string.Empty;
    public string SourceRoot { get; set; } = string.Empty;
    public string ProjectIdentity { get; set; } = string.Empty;
    public string PackageVersion { get; set; } = string.Empty;
    public string Configuration { get; set; } = string.Empty;
    public string TargetFramework { get; set; } = string.Empty;
    public string RuntimeIdentifier { get; set; } = string.Empty;
    public string AssemblyName { get; set; } = string.Empty;
    public string AssetsDirectory { get; set; } = string.Empty;
    public string OwnedContractsPath { get; set; } = string.Empty;
    public string ContractLockPath { get; set; } = string.Empty;
    public bool AllowDevelopmentRegistryOrigin { get; set; }
    public ITaskItem[] SourceFiles { get; set; } = [];
    public ITaskItem[] ResourceItems { get; set; } = [];
    public ITaskItem[] PackageReferences { get; set; } = [];
    public ITaskItem[] ProjectReferences { get; set; } = [];
    public ITaskItem[] References { get; set; } = [];
    public ITaskItem[] ProjectImports { get; set; } = [];
    public ITaskItem[] GeneratorAnalyzers { get; set; } = [];
    public ITaskItem[] Properties { get; set; } = [];
    public ITaskItem[] Usings { get; set; } = [];
    public ITaskItem[] CapturedInputs { get; set; } = [];
    public string RolePlanPath { get; set; } = string.Empty;
    public string RolePlanSha256 { get; set; } = string.Empty;
    /// <summary>Gets or sets the SDK-generated global-using source included in the native compilation plan.</summary>
    public string GeneratedGlobalUsingsFile { get; set; } = string.Empty;
    public string DevPackagePath { get; set; } = string.Empty;
    public string SelectionFile { get; set; } = string.Empty;
    public string PackageOutputPath { get; set; } = string.Empty;
    public string[] ExpectedRuntimeIdentifiers { get; set; } = [];
    public ITaskItem[] ValidatedInputs { get; set; } = [];
    [Output] public ITaskItem[] Result { get; private set; } = [];
    internal System.Action? BeforeArchivePublication { get; set; }
    internal System.Action? BeforeInputLeaseAcquisition { get; set; }
    public void Cancel() => _cancellation.Cancel();

    public override bool Execute()
    {
        Result = [];
        try
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            switch (Action)
            {
                case "Capture": Result = [Capture()]; break;
                case "Export": Result = [Export()]; break;
                case "Validate": Result = [Validate()]; break;
                case "Pack": Pack(); break;
                default: throw Invalid("Unknown role-input operation.");
            }
            return !Log.HasLoggedErrors;
        }
        catch (Exception error) { Log.LogErrorFromException(error, showStackTrace: false); return false; }
    }

    private ITaskItem Capture()
    {
        var project = Full(ProjectDirectory);
        var sourceRoot = SourceRoot.Length == 0 ? DefaultSourceRoot(project) : Full(Path.GetFullPath(SourceRoot, project));
        var baselinePath = Full(Path.GetFullPath(OwnedContractsPath.Length == 0 ? SunderRpcAuthoringDocuments.OwnedContractsFileName : OwnedContractsPath, project));
        var lockPath = Full(Path.GetFullPath(ContractLockPath.Length == 0 ? SunderRpcAuthoringDocuments.ContractLockFileName : ContractLockPath, project));
        var baseline = GeneratedOutputPathSafety.PathExists(baselinePath) ? SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(baselinePath) : null;
        if (baseline is null) GenerateSunderRpcBindingsTask.VerifyNoLostPublicBaseline(project, Path.Combine(project, "obj", GenerateSunderRpcBindingsTask.GeneratedDirectoryName));
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(lockPath, AllowDevelopmentRegistryOrigin);
        if (!Rids.Contains(RuntimeIdentifier) || !DotNetWorkerProfile.IsSupported(TargetFramework) || string.IsNullOrWhiteSpace(ProjectIdentity)
            || string.IsNullOrWhiteSpace(AssemblyName) || string.IsNullOrWhiteSpace(Configuration)) throw Invalid("An exact project/configuration/framework/RID is required.");
        var sourceTree = Tree(sourceRoot, source: true, _cancellation.Token);
        var external = new SortedDictionary<string, SunderPrebuiltFile>(StringComparer.Ordinal);
        var locations = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (baseline is null)
        {
            var key = "baseline-absent:" + Relative(sourceRoot, baselinePath);
            external.Add(key, new(key, 0, Hash([]))); locations.Add(key, baselinePath);
        }
        var assetsPath = Full(Path.GetFullPath(ProjectAssetsFile, project));
        var restore = SunderPrebuiltRestoreInputs.Capture(assetsPath, TargetFramework, RuntimeIdentifier, _cancellation.Token);
        external.Add(restore.Path, restore); locations.Add(restore.Path, assetsPath);
        var assetDirectory = Full(Path.GetFullPath(AssetsDirectory.Length == 0 ? "Assets" : AssetsDirectory, project));
        var assetKey = "assets:" + Relative(sourceRoot, assetDirectory);
        external.Add(assetKey, AssetTree(assetDirectory, assetKey)); locations.Add(assetKey, assetDirectory);
        var items = new List<SunderPrebuiltItem>();
        void Add(string kind, ITaskItem item, bool authored = false)
        {
            var identity = item.ItemSpec;
            if (authored)
            {
                var path = Full(Path.GetFullPath(identity, project)); identity = Relative(sourceRoot, path);
                Track(kind + ":" + identity, path);
            }
            var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in new[] { "Aliases", "Link", "Version", "VersionOverride", "PrivateAssets", "IncludeAssets", "ExcludeAssets", "ReferenceOutputAssembly",
                         "AdditionalProperties", "GlobalPropertiesToRemove", "Private", "SunderPackageAssetRole", "SunderPackageResourceOwner", "LogicalName", "TargetPath", "CopyToOutputDirectory", "CopyToPublishDirectory", "Static", "Alias", "ResourceKind" })
                if (item.GetMetadata(name).Length != 0) metadata.Add(name, item.GetMetadata(name).Replace('\\', '/'));
            items.Add(new(kind, identity.Replace('\\', '/'), metadata));
        }
        void Track(string name, string path)
        {
            var file = File(path, name, 64 * 1024 * 1024, _cancellation.Token);
            if (external.TryGetValue(name, out var previous)) Equal(previous, file, "External input identity");
            external[name] = file; locations[name] = Full(path);
        }
        foreach (var source in SourceFiles.Where(item => !item.GetMetadata("AutoGen").Equals("true", StringComparison.OrdinalIgnoreCase))) Add("compile", source, authored: true);
        foreach (var resource in ResourceItems)
        {
            var kind = resource.GetMetadata("ResourceKind");
            if (kind is "None" or "Content" && !SunderPackageResourcePlanner.Copies(resource.GetMetadata("CopyToOutputDirectory"))
                && !SunderPackageResourcePlanner.Copies(resource.GetMetadata("CopyToPublishDirectory"))) continue;
            Add("resource", resource, authored: true);
        }
        foreach (var reference in PackageReferences) Add("package", reference);
        foreach (var reference in ProjectReferences.Where(item => !item.GetMetadata("ReferenceOutputAssembly").Equals("false", StringComparison.OrdinalIgnoreCase)))
        {
            var path = Full(Path.GetFullPath(reference.ItemSpec, project)); var name = Path.GetFileName(path);
            var identity = GeneratedOutputPathSafety.IsDescendantOf(path, sourceRoot) ? Relative(sourceRoot, path) : "external-project/" + name;
            var clone = new TaskItem(reference) { ItemSpec = identity }; Add("project", clone);
            Track("project:" + identity, path);
            // Source references retain a closed source-tree dependency, not just a
            // compiler DLL that might have been built from different helper source.
            var tree = Tree(Path.GetDirectoryName(path)!, source: true, _cancellation.Token);
            var key = "project-tree:" + identity;
            external.Add(key, new(key, tree.Sum(file => file.Bytes), Hash(Write(tree)))); locations.Add(key, Path.GetDirectoryName(path)!);
        }
        foreach (var reference in References)
        {
            Add("reference", reference);
            var hint = reference.GetMetadata("HintPath");
            if (hint.Length != 0) Track("reference:" + reference.ItemSpec, Full(Path.GetFullPath(hint, project)));
        }
        foreach (var import in ProjectImports)
        {
            var path = Full(Path.GetFullPath(import.ItemSpec, project));
            // MSBuildAllProjects includes the entry project only in some request
            // frames. Its exact bytes are always bound by the source-tree record.
            if (path == Full(ProjectFile)) continue;
            if (path.EndsWith(".nuget.g.props", StringComparison.Ordinal) || path.EndsWith(".nuget.g.targets", StringComparison.Ordinal)) continue;
            if (GeneratedOutputPathSafety.IsDescendantOf(path, sourceRoot)) Track("import:" + Relative(sourceRoot, path), path);
            // External tool/SDK imports are identified by content, never by their
            // native installation prefix. Duplicate names with different bytes fail.
            else Track("import:" + Path.GetFileName(path), path);
        }
        foreach (var analyzer in GeneratorAnalyzers) Track("generator:" + Path.GetFileName(analyzer.ItemSpec), Full(analyzer.ItemSpec));
        foreach (var item in Usings) Add("using", item);
        var properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in Properties) properties.Add(property.ItemSpec, property.GetMetadata("Value"));
        var toolDirectory = Path.GetDirectoryName(typeof(ManageSunderPackageRoleInputsTask).Assembly.Location)!;
        var tools = new[] { "Sunder.Package.Build.dll", "Sunder.Package.Build.Editor.dll", "Sunder.Package.Format.dll", "Sunder.Sdk.dll",
            "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll", "Microsoft.Extensions.DependencyInjection.Abstractions.dll", "Microsoft.Extensions.Logging.Abstractions.dll" }
            .Order(StringComparer.Ordinal).Select(name => File(Path.Combine(toolDirectory, name), name, 64 * 1024 * 1024, _cancellation.Token)).ToArray();
        if (items.Count > MaximumFiles || external.Count > MaximumFiles) throw Invalid("Evaluated inputs exceed their item budget.");
        var frame = new SunderPrebuiltFrame(baseline?.PackageId ?? string.Empty, PackageVersion, Relative(sourceRoot, ProjectFile), ProjectIdentity,
            Configuration, TargetFramework, RuntimeIdentifier, AssemblyName, Relative(sourceRoot, baselinePath), baseline?.Sha256 ?? Hash([]),
            Relative(sourceRoot, lockPath), contractLock.Sha256, properties,
            items.OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Identity, StringComparer.Ordinal).ToArray(),
            sourceTree, tools, external.Values.ToArray());
        var result = new TaskItem(ProjectFile);
        result.SetMetadata("Frame", Convert.ToBase64String(Write(frame)));
        result.SetMetadata("Sha256", Hash(Write(frame)));
        result.SetMetadata("SourceRoot", sourceRoot);
        result.SetMetadata("ExternalLocations", Convert.ToBase64String(Write(locations)));
        result.SetMetadata("ToolDirectory", toolDirectory);
        VerifyCapture(result);
        return result;
    }

    private void VerifyCapture(ITaskItem input)
    {
        var frame = Frame(input);
        Equal(frame.Sources, Tree(input.GetMetadata("SourceRoot"), source: true, _cancellation.Token), "Source bytes/membership");
        var locations = Read<SortedDictionary<string, string>>(Convert.FromBase64String(input.GetMetadata("ExternalLocations")));
        foreach (var expected in frame.ExternalInputs)
        {
            var path = locations[expected.Path];
            if (expected.Path.StartsWith("baseline-absent:", StringComparison.Ordinal))
            {
                if (GeneratedOutputPathSafety.PathExists(path)) throw Invalid("The absent private-only baseline appeared during qualification.");
            }
            else if (expected.Path == "restore:graph")
                Equal(expected, SunderPrebuiltRestoreInputs.Capture(path, frame.TargetFramework, frame.RuntimeIdentifier, _cancellation.Token), "Restored dependency selection");
            else if (expected.Path.StartsWith("assets:", StringComparison.Ordinal))
                Equal(expected, AssetTree(path, expected.Path), "Package asset bytes/membership");
            else if (expected.Path.StartsWith("project-tree:", StringComparison.Ordinal))
            {
                var tree = Tree(path, source: true, _cancellation.Token);
                Equal(expected, new SunderPrebuiltFile(expected.Path, tree.Sum(file => file.Bytes), Hash(Write(tree))), "Source dependency tree");
            }
            else Equal(expected, File(path, expected.Path, 64 * 1024 * 1024, _cancellation.Token), "External input");
        }
        foreach (var tool in frame.Toolset) Equal(tool, File(Path.Combine(input.GetMetadata("ToolDirectory"), tool.Path), tool.Path, 64 * 1024 * 1024, _cancellation.Token), "Toolset");
    }
    private static SunderPrebuiltFrame Frame(ITaskItem input)
    {
        var bytes = Convert.FromBase64String(input.GetMetadata("Frame"));
        if (Hash(bytes) != input.GetMetadata("Sha256")) throw Invalid("Captured input frame changed.");
        return Read<SunderPrebuiltFrame>(bytes);
    }

    private static ITaskItem BindPrivatePackageIdentity(ITaskItem input, string packageId)
    {
        var frame = Frame(input);
        if (frame.PackageId.Length != 0) return input;
        if (frame.OwnedBaselineSha256 != Hash([]) || !frame.ExternalInputs.Any(file => file.Path == "baseline-absent:" + frame.OwnedBaseline))
            throw Invalid("Only an explicitly absent baseline can use the native source-derived package identity.");
        var copy = new TaskItem(input); var bytes = Write(frame with { PackageId = packageId });
        copy.SetMetadata("Frame", Convert.ToBase64String(bytes)); copy.SetMetadata("Sha256", Hash(bytes));
        return copy;
    }
    private SunderPrebuiltFile AssetTree(string directory, string identity)
    {
        GeneratedOutputPathSafety.EnsureSafeAncestors(directory, includeLeaf: true, "package assets");
        if (System.IO.File.Exists(directory)) throw Invalid("The package asset directory is a file.");
        var files = Directory.Exists(directory) ? Tree(directory, source: false, _cancellation.Token) : [];
        var bytes = files.Sum(file => file.Bytes);
        if (bytes > MaximumSourceBytes) throw Invalid("Package assets exceed their source byte budget.");
        return new(identity, bytes, Hash(Write(files)));
    }
    private string DefaultSourceRoot(string project)
    {
        for (var directory = new DirectoryInfo(project); directory is not null; directory = directory.Parent)
            if (System.IO.File.Exists(Path.Combine(directory.FullName, ".git")) || Directory.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
        var root = project;
        if (AssetsDirectory.Length != 0)
        {
            var assets = Full(Path.GetFullPath(AssetsDirectory, project));
            while (assets != root && !GeneratedOutputPathSafety.IsDescendantOf(assets, root))
                root = Path.GetDirectoryName(root) ?? throw Invalid("Set a bounded reviewed source root for package assets.");
        }
        foreach (var input in SourceFiles.Concat(ResourceItems.Where(item => item.GetMetadata("ResourceKind") is not ("None" or "Content")
                     || SunderPackageResourcePlanner.Copies(item.GetMetadata("CopyToOutputDirectory")) || SunderPackageResourcePlanner.Copies(item.GetMetadata("CopyToPublishDirectory")))))
        {
            var path = Full(Path.GetFullPath(input.ItemSpec, project));
            while (!GeneratedOutputPathSafety.IsDescendantOf(path, root))
                root = Path.GetDirectoryName(root) ?? throw Invalid("Set a bounded reviewed source root for linked inputs.");
        }
        if (root == Path.GetPathRoot(root)) throw Invalid("A filesystem root cannot be the reviewed source root.");
        return root;
    }
}
