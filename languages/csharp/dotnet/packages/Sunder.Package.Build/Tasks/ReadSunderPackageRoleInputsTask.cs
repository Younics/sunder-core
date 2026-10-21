using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Sdk.Storage;

namespace Sunder.Package.Build.Tasks;

/// <summary>Reads one immutable single-project compilation plan and validates its exact source inputs before an inner role build.</summary>
public sealed partial class ReadSunderPackageRoleInputsTask : Microsoft.Build.Utilities.Task
{
    private ITaskItem[] _sourceFiles = [];
    private bool _sourceEvaluationSupplied;

    /// <summary>Gets or sets the path to the generated role plan.</summary>
    [Required]
    public string PlanPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the exact plan SHA-256 provided by the producing package analysis.</summary>
    [Required]
    public string ExpectedSha256 { get; set; } = string.Empty;

    /// <summary>Gets or sets the expected canonical authored project identity.</summary>
    [Required]
    public string ProjectIdentity { get; set; } = string.Empty;

    /// <summary>Gets or sets the internal role, app or worker.</summary>
    [Required]
    public string Role { get; set; } = string.Empty;

    /// <summary>Gets or sets the caller-owned output lease retained through role compilation.</summary>
    [Required]
    public string OutputLockLeaseId { get; set; } = string.Empty;

    /// <summary>Gets or sets the expected build configuration, when invoked by the package build pipeline.</summary>
    public string Configuration { get; set; } = string.Empty;

    /// <summary>Gets or sets the expected target framework, when invoked by the package build pipeline.</summary>
    public string TargetFramework { get; set; } = string.Empty;
    public string RuntimeIdentifier { get; set; } = string.Empty;
    public string PackageVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the ordinary evaluated source inputs, before role projection replaces Compile items.</summary>
    public ITaskItem[] SourceFiles
    {
        get => _sourceFiles;
        set
        {
            _sourceFiles = value ?? throw new ArgumentNullException(nameof(value));
            _sourceEvaluationSupplied = true;
        }
    }

    public string ProjectDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the SDK-generated global-using input in this role's intermediate directory.</summary>
    public string GeneratedGlobalUsingsFile { get; set; } = string.Empty;

    /// <summary>Gets or sets that SDK input's original analysis path; its contents must still match the source snapshot.</summary>
    public string AnalysisGlobalUsingsFile { get; set; } = string.Empty;

    /// <summary>Gets or sets whether an evaluated Compile set must be checked, including when MSBuild supplies an empty item list.</summary>
    public bool ValidateSourceEvaluation { get; set; }

    public string DefineConstants { get; set; } = string.Empty;
    public ITaskItem[] EvaluatedResources { get; set; } = [];
    public bool ValidateResourceEvaluation { get; set; }
    public ITaskItem[] EvaluatedGeneratorProperties { get; set; } = [];

    /// <summary>Gets the validated role-local compilation inputs.</summary>
    [Output]
    public ITaskItem[] CompileItems { get; private set; } = [];

    /// <summary>Gets the authored package assembly name for the requested role.</summary>
    [Output]
    public string AssemblyName { get; private set; } = string.Empty;

    /// <summary>Gets the validated managed reference closure for the requested role.</summary>
    [Output]
    public ITaskItem[] ReferencePaths { get; private set; } = [];

    [Output]
    public ITaskItem[] RuntimeReferencePaths { get; private set; } = [];

    public override bool Execute()
    {
        CompileItems = []; ReferencePaths = []; RuntimeReferencePaths = []; AssemblyName = string.Empty;
        BindingSnapshots = []; ContractBundles = []; RpcProviders = []; RpcUses = []; AuthoringReceipts = [];
        ProjectAssetsFile = string.Empty; ProjectExtensionsPath = string.Empty;
        CompileAssetPaths = []; RuntimeAssetPaths = []; ExcludedProjectReferences = [];
        ResourceItems = []; ExcludedGenerators = [];
        try
        {
            using var reads = new SunderBuildReadSnapshot();
            if (Role is not ("app" or "worker")) throw new InvalidDataException("The package compilation role must be app or worker.");
            if (ExpectedSha256.Length != 64 || ExpectedSha256.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException("An exact lowercase role-plan SHA-256 is required.");
            var path = Path.GetFullPath(PlanPath);
            var root = Path.GetDirectoryName(path)!;
            GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "package role plan");
            if (!ManageSunderRpcGeneratedOutputLockTask.Covers(BuildEngine, OutputLockLeaseId, [root]))
                throw new InvalidDataException("The role compilation requires a caller-owned lease covering its generated sources.");
            var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, 32 * 1024 * 1024, "package role plan");
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ExpectedSha256) throw new InvalidDataException("The package role plan changed after package analysis.");
            using var document = JsonDocument.Parse(bytes);
            var plan = document.RootElement;
            if (plan.GetProperty("schemaVersion").GetInt32() != 1 || plan.GetProperty("projectIdentity").GetString() != ProjectIdentity)
                throw new InvalidDataException("The role plan does not match the authored project.");
            if (Configuration.Length != 0 && plan.GetProperty("configuration").GetString() != Configuration
                || TargetFramework.Length != 0 && plan.GetProperty("targetFramework").GetString() != TargetFramework
                || RuntimeIdentifier.Length != 0 && plan.GetProperty("runtimeIdentifier").GetString() != RuntimeIdentifier
                || PackageVersion.Length != 0 && plan.GetProperty("packageVersion").GetString() != PackageVersion)
                throw new InvalidDataException("The role plan does not match the build configuration, framework, RID and package version.");
            if (Role == "app" && !plan.GetProperty("hasApp").GetBoolean()) throw new InvalidDataException("The package has no generated App role.");
            // The dependency/resource prepass has a project root but deliberately
            // omits Compile evaluation. Only the compilation read supplies this
            // parameter; an explicitly supplied empty set must still be checked.
            if (_sourceEvaluationSupplied || ValidateSourceEvaluation)
            {
                var reader = new GenerateSunderRpcBindingsTask { BuildEngine = BuildEngine, SourceFiles = SourceFiles };
                var sources = reader.ReadSourceFiles(Path.GetFullPath(ProjectDirectory))
                    ?? throw new InvalidDataException("The evaluated role sources could not be read.");
                var globalUsings = string.IsNullOrWhiteSpace(GeneratedGlobalUsingsFile) ? null : Path.GetFullPath(GeneratedGlobalUsingsFile, ProjectDirectory);
                var analysisUsings = string.IsNullOrWhiteSpace(AnalysisGlobalUsingsFile) ? null : Path.GetFullPath(AnalysisGlobalUsingsFile, ProjectDirectory);
                var actual = sources.Select(source => (Path: Path.GetRelativePath(Path.GetFullPath(ProjectDirectory),
                        globalUsings is not null && analysisUsings is not null && string.Equals(source.Path, globalUsings, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                            ? analysisUsings : source.Path).Replace('\\', '/'),
                    Hash: Convert.ToHexStringLower(SHA256.HashData(SunderRpcDescriptorFileReader.ReadStableBytes(source.Path, 4_194_304, "evaluated role source")))))
                    .OrderBy(value => value.Path, StringComparer.Ordinal).ToArray();
                var expected = plan.GetProperty("sourceFiles").EnumerateArray().Select(value =>
                    (Path: value.GetProperty("path").GetString()!, Hash: value.GetProperty("sha256").GetString()!)).OrderBy(value => value.Path, StringComparer.Ordinal).ToArray();
                if (!actual.SequenceEqual(expected))
                    throw new InvalidDataException("The evaluated source set changed between package analysis and role compilation. Missing: "
                        + string.Join(", ", expected.Select(value => value.Path).Except(actual.Select(value => value.Path), StringComparer.Ordinal).Take(16))
                        + "; added: " + string.Join(", ", actual.Select(value => value.Path).Except(expected.Select(value => value.Path), StringComparer.Ordinal).Take(16))
                        + "; changed: " + string.Join(", ", actual.Where(value => expected.Any(previous => previous.Path == value.Path && previous.Hash != value.Hash)).Select(value => value.Path).Take(16)));
                if (!actual.Select(value => value.Hash).Order(StringComparer.Ordinal).SequenceEqual(
                    plan.GetProperty("sourceSha256").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal), StringComparer.Ordinal))
                    throw new InvalidDataException("The role plan source hash inventory differs from its exact source membership.");
                var constants = DefineConstants.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                if (!constants.SequenceEqual(plan.GetProperty("defineConstants").EnumerateArray().Select(value => value.GetString()), StringComparer.Ordinal))
                    throw new InvalidDataException("The compilation constants changed between package analysis and role compilation.");
            }
            var files = plan.GetProperty(Role + "Compile");
            ValidateGeneratedSourceMembership(plan, root);
            if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is < 1 or > 16384) throw new InvalidDataException("The role plan has an invalid compilation input count.");
            var items = new List<ITaskItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            foreach (var file in files.EnumerateArray())
            {
                var relative = file.GetProperty("path").GetString();
                var hash = file.GetProperty("sha256").GetString();
                if (relative is null || !PackageStorageValidation.IsValidRelativePath(relative) || !relative.EndsWith(".cs", StringComparison.Ordinal)
                    || !seen.Add(relative) || hash is null || hash.Length != 64)
                    throw new InvalidDataException("The role plan contains an invalid or duplicate source path/hash.");
                var sourcePath = Path.Combine(root, relative);
                GeneratedOutputPathSafety.EnsureSafeAncestors(sourcePath, includeLeaf: true, "package role source");
                var source = SunderRpcDescriptorFileReader.ReadStableBytes(sourcePath, 16 * 1024 * 1024, "package role source");
                if ((totalBytes += source.Length) > 64L * 1024 * 1024) throw new InvalidDataException("The package role source set exceeds its byte budget.");
                if (Convert.ToHexStringLower(SHA256.HashData(source)) != hash) throw new InvalidDataException("A package role source changed after package analysis.");
                items.Add(new TaskItem(sourcePath));
            }
            var name = plan.GetProperty("assemblyName").GetString();
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("The role plan has no assembly identity.");
            var references = ReadReferences(plan.GetProperty(Role + "References"));
            var runtimeReferences = ReadReferences(plan.GetProperty(Role + "RuntimeReferences"));
            var dependencies = ReadDependencies(plan, root);
            var resources = ReadResources(plan, root);
            ReadEvidence(plan, root);
            CompileItems = items.ToArray(); AssemblyName = Role == "app" ? name + ".App" : name;
            ReferencePaths = references; RuntimeReferencePaths = runtimeReferences;
            ResourceItems = resources;
            ExcludedGenerators = plan.TryGetProperty("frameworkGenerators", out var generators)
                ? generators.EnumerateArray().Select(value => (ITaskItem)new TaskItem(value.GetString()!)).ToArray() : [];
            if (dependencies is not null)
            {
                ProjectAssetsFile = dependencies.AssetsFile; ProjectExtensionsPath = dependencies.ExtensionsPath;
                CompileAssetPaths = dependencies.Compile; RuntimeAssetPaths = dependencies.Runtime; ExcludedProjectReferences = dependencies.ExcludedProjects;
            }
            // Validate the complete current plan above even if this role's compiler can reuse
            // its prior output. Receipt/projection evidence continues to use that full plan.
            reads.Complete();
            var compilationRoot = SunderRoleCompilationSnapshot.Publish(plan, root, Role);
            if (compilationRoot != root)
            {
                CompileItems = CompileItems.Select(Remap).ToArray();
                ResourceItems = ResourceItems.Select(Remap).ToArray();
                if (ProjectAssetsFile.Length != 0) ProjectAssetsFile = RemapPath(ProjectAssetsFile);
                if (ProjectExtensionsPath.Length != 0) ProjectExtensionsPath = RemapPath(ProjectExtensionsPath);
            }
            return true;

            string RemapPath(string value) => Path.Combine(compilationRoot, Path.GetRelativePath(root, value));
            ITaskItem Remap(ITaskItem value) => new TaskItem(value) { ItemSpec = RemapPath(value.ItemSpec) };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException)
        {
            Log.LogError("package-authoring.role-plan-invalid: " + error.Message);
            return false;
        }
    }

    private static ITaskItem[] ReadReferences(JsonElement values)
    {
        var references = new List<ITaskItem>();
        var referenceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in values.EnumerateArray())
        {
            if (references.Count == 4096) throw new InvalidDataException("The package role reference set exceeds its input budget.");
            var referencePath = reference.GetProperty("path").GetString();
            var referenceName = reference.GetProperty("name").GetString();
            var hash = reference.GetProperty("sha256").GetString();
            if (referencePath is null || !Path.IsPathFullyQualified(referencePath) || string.IsNullOrWhiteSpace(referenceName) || !referenceNames.Add(referenceName))
                throw new InvalidDataException("The role plan contains an invalid or duplicate reference.");
            var contents = SunderRpcDescriptorFileReader.ReadStableBytes(referencePath, int.MaxValue, "package role reference");
            if (Convert.ToHexStringLower(SHA256.HashData(contents)) != hash) throw new InvalidDataException("A package role reference changed after package analysis.");
            var item = new TaskItem(referencePath); item.SetMetadata("AssemblyName", referenceName); references.Add(item);
        }
        return references.ToArray();
    }
}
