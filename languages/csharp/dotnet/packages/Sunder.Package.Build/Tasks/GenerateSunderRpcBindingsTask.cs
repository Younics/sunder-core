using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Build.Tasks;

/// <summary>Generates deterministic C# bindings and immutable canonical descriptor snapshots, including ABI identity.</summary>
public sealed partial class GenerateSunderRpcBindingsTask : Microsoft.Build.Utilities.Task
{
    internal const string GeneratedDirectoryName = "sunder-rpc";
    internal const string SnapshotDirectoryName = ".snapshots";
    internal const string MarkerContents = "Sunder.Package.Build RPC generated output v1\n";

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ITaskItem[] Bindings { get; set; } = [];

    public ITaskItem[] SourceFiles { get; set; } = [];

    public ITaskItem[] ReferencePaths { get; set; } = [];

    public ITaskItem[] ExpectedPackageProjectExports { get; set; } = [];

    public string GenerationMode { get; set; } = "Combined";

    public string PackageId { get; set; } = string.Empty;

    public string PackageVersion { get; set; } = string.Empty;

    public string ProjectIdentity { get; set; } = string.Empty;

    public string AssemblyName { get; set; } = "Sunder.Rpc.Authoring";

    public string Configuration { get; set; } = string.Empty;

    public string TargetFramework { get; set; } = string.Empty;

    public string DefineConstants { get; set; } = string.Empty;

    public string OutputType { get; set; } = string.Empty;

    public bool NullableEnabled { get; set; } = true;

    /// <summary>Produces validated single-project source-role plans and compilation inputs with the RPC output transaction.</summary>
    public bool GeneratePackageRoles { get; set; }

    public ITaskItem[] RuntimeReferencePaths { get; set; } = [];

    public string ProjectAssetsFile { get; set; } = string.Empty;
    public string ProjectFile { get; set; } = string.Empty;
    public string ProjectExtensionsPath { get; set; } = string.Empty;
    public string RuntimeIdentifier { get; set; } = string.Empty;
    public ITaskItem[] PackageReferences { get; set; } = [];
    public ITaskItem[] ProjectReferences { get; set; } = [];
    public ITaskItem[] ProjectImports { get; set; } = [];
    public ITaskItem[] ResourceItems { get; set; } = [];
    public ITaskItem[] GeneratorAnalyzers { get; set; } = [];
    public ITaskItem[] GeneratorProperties { get; set; } = [];

    public string RootNamespace { get; set; } = string.Empty;

    /// <summary>Gets the complete generated Worker compilation inputs for a single-project package.</summary>
    [Output]
    public ITaskItem[] WorkerCompileItems { get; private set; } = [];

    /// <summary>Gets the complete generated App compilation inputs, or an empty list for a headless package.</summary>
    [Output]
    public ITaskItem[] AppCompileItems { get; private set; } = [];

    /// <summary>Gets the validated internal package role plan and its exact hash.</summary>
    [Output]
    public ITaskItem[] PackageRolePlans { get; private set; } = [];

    public string OwnedContractsPath { get; set; } = string.Empty;

    public string ContractLockPath { get; set; } = string.Empty;

    public string ContractCacheRoot { get; set; } = SunderRpcContractCache.DefaultRoot;
    public bool AllowDevelopmentRegistryOrigin { get; set; }

    public string OutputLockLeaseId { get; set; } = string.Empty;

    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    [Required]
    public string IntermediateOutputPath { get; set; } = string.Empty;

    [Output]
    public ITaskItem[] CompileItems { get; private set; } = [];

    [Output]
    public ITaskItem[] FileWrites { get; private set; } = [];

    [Output]
    public ITaskItem[] BindingSnapshots { get; private set; } = [];

    [Output]
    public ITaskItem[] ContractBundles { get; private set; } = [];

    [Output]
    public ITaskItem[] RpcProviders { get; private set; } = [];

    [Output]
    public ITaskItem[] RpcUses { get; private set; } = [];

    [Output]
    public ITaskItem[] PackageProjectExports { get; private set; } = [];

    [Output]
    public ITaskItem[] AuthoringReceipts { get; private set; } = [];

    public override bool Execute()
    {
        CompileItems = [];
        FileWrites = [];
        BindingSnapshots = [];
        ContractBundles = [];
        RpcProviders = [];
        RpcUses = [];
        PackageProjectExports = [];
        AuthoringReceipts = [];
        WorkerCompileItems = [];
        AppCompileItems = [];
        PackageRolePlans = [];
        if (!string.IsNullOrEmpty(ContractOperation)) return ExecuteOwnedContractOperation();
        string? stagingPath = null;
        string? stagingMarkerPath = null;

        try
        {
            if (!TryResolveGenerationMode(out var generationMode)) return false;
            var designTime = generationMode == RpcGenerationMode.EditorImports;
            if (designTime && (Bindings.Length != 0 || ExpectedPackageProjectExports.Length != 0 || !GeneratePackageRoles))
            {
                Log.LogError("package-authoring.design-time-invalid: Editor generation requires single-project source, without descriptor bindings or project imports.");
                return false;
            }
            if (GeneratePackageRoles && generationMode == RpcGenerationMode.ProjectImport)
            {
                Log.LogError("package-authoring.source-required: Role planning requires the complete authored package source, not descriptor-only project imports.");
                return false;
            }
            if (!TryResolveConfiguration(
                    out var projectDirectory,
                    out var intermediateOutputPath,
                    out var generatedDirectory,
                    out var markerPath))
            {
                return false;
            }
            if (designTime != (Path.GetFileName(Path.TrimEndingDirectorySeparator(intermediateOutputPath)) == "sunder-design-time"))
            {
                Log.LogError("package-authoring.design-time-invalid: Editor generation must use its isolated sunder-design-time intermediate directory; artifact generation cannot use that directory.");
                return false;
            }

            var descriptorInputs = new Dictionary<string, SunderRpcDescriptorFileSnapshot>(PathComparer);
            var plans = generationMode is RpcGenerationMode.ProjectExport or RpcGenerationMode.ProjectImport
                ? []
                : BuildPlans(projectDirectory, generatedDirectory, descriptorInputs);
            if (plans is null) return false;
            if ((generationMode is RpcGenerationMode.ProjectExport or RpcGenerationMode.ProjectImport)
                && Bindings.Length != 0)
            {
                Log.LogError($"{generationMode} mode does not accept descriptor-first SunderRpcBinding items.");
                return false;
            }
            var completeSourcePlan = generationMode == RpcGenerationMode.ProjectImport
                ? SourceAuthoringPlan.Empty
                : generationMode == RpcGenerationMode.EditorImports
                ? BuildEditorImportPlan(projectDirectory, generatedDirectory)
                : BuildSourceAuthoringPlan(projectDirectory, generatedDirectory);
            if (completeSourcePlan is null) return false;
            var sourcePlan = SelectSourceOutputs(completeSourcePlan, generationMode);

            var discoveryKeys = new List<string>
            {
                GeneratedOutputLock.TargetLeafDiscoveryKey(intermediateOutputPath, generatedDirectory),
            };
            var lockedOutputs = new List<string> { generatedDirectory };
            var expectedExport = ResolveExpectedPackageProjectExport(
                generationMode,
                projectDirectory,
                generatedDirectory,
                completeSourcePlan);
            if (Log.HasLoggedErrors) return false;
            if (expectedExport is not null)
            {
                discoveryKeys.Add(GeneratedOutputLock.TargetLeafDiscoveryKey(
                    expectedExport.IntermediateOutputPath,
                    expectedExport.GeneratedDirectory));
                lockedOutputs.Add(expectedExport.GeneratedDirectory);
            }
            if (!string.IsNullOrWhiteSpace(OutputLockLeaseId)
                && !ManageSunderRpcGeneratedOutputLockTask.Covers(
                    BuildEngine,
                    OutputLockLeaseId,
                    lockedOutputs))
            {
                Log.LogError(
                    $"RPC generated-output lock lease '{OutputLockLeaseId}' does not cover every generated output used by this task.");
                return false;
            }
            using var outputLock = string.IsNullOrWhiteSpace(OutputLockLeaseId)
                ? GeneratedOutputLock.Acquire(discoveryKeys, lockedOutputs)
                : null;

            SunderRpcPackageProjectExport? importedExport = null;
            if (expectedExport is not null)
            {
                importedExport = ValidateExpectedPackageProjectExport(expectedExport);
            }
            if (generationMode == RpcGenerationMode.ProjectImport)
            {
                if (importedExport is null)
                {
                    Log.LogError("ProjectImport mode requires one validated project-neutral RPC export.");
                    return false;
                }
                sourcePlan = BuildProjectImportPlan(generatedDirectory, importedExport);
            }

            var duplicateOutput = plans.Select(static plan => plan.RelativeOutputPath)
                .Concat(sourcePlan.Files.Select(static plan => plan.RelativeOutputPath))
                .GroupBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(static group => group.Count() > 1);
            if (duplicateOutput is not null)
            {
                Log.LogError(
                    $"Sunder RPC generation produced more than one output named '{duplicateOutput.Key}'.");
                return false;
            }

            GeneratedOutputPathSafety.EnsureRegularDirectory(
                intermediateOutputPath,
                "Sunder RPC intermediate output directory");
            GeneratedOutputPathSafety.EnsureSafeAncestors(
                generatedDirectory,
                includeLeaf: true,
                "Sunder RPC generated directory");
            GeneratedOutputPathSafety.EnsureSafeAncestors(
                markerPath,
                includeLeaf: true,
                "Sunder RPC generated-output marker");

            GeneratedOutputTransaction.RecoverAndCleanup(generatedDirectory, markerPath);
            if (!TryGetOwnedOutputState(generatedDirectory, markerPath, out var hasOwnedOutput))
            {
                return false;
            }
            if (!hasOwnedOutput && plans.Count == 0 && sourcePlan.Files.Count == 0)
            {
                return true;
            }

            completeSourcePlan.Inputs?.Verify();
            var immutableExportPath = generationMode == RpcGenerationMode.ProjectExport
                ? PublishImmutableProjectExport(intermediateOutputPath, sourcePlan)
                : null;

            stagingPath = generatedDirectory + ".stage-" + Guid.NewGuid().ToString("N");
            stagingMarkerPath = markerPath + ".stage-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stagingPath);
            GeneratedOutputPathSafety.EnsureRegularDirectory(stagingPath, "staged Sunder RPC generated directory");

            foreach (var plan in plans.OrderBy(static plan => plan.OutputPath, StringComparer.Ordinal))
            {
                WriteStagedFile(stagingPath, plan.RelativeOutputPath, plan.SourceUtf8);
            }
            foreach (var plan in sourcePlan.Files.OrderBy(static plan => plan.OutputPath, StringComparer.Ordinal))
            {
                WriteStagedFile(stagingPath, plan.RelativeOutputPath, plan.Contents);
                if (plan.RelativeOutputPath == SunderRpcAuthoringDocuments.AuthoringReceiptFileName)
                {
                    SunderRpcAuthoringDocuments.ValidateAuthoringReceipt(
                        Path.Combine(stagingPath, plan.RelativeOutputPath), plan.Contents);
                }
            }

            var snapshotPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            var snapshotContents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var input in descriptorInputs.Values.OrderBy(static input => input.SourcePath, PathComparer))
            {
                var relativePath = SnapshotRelativePath(input.Descriptor);
                if (snapshotContents.TryGetValue(relativePath, out var existingBytes))
                {
                    if (!existingBytes.AsSpan().SequenceEqual(input.CanonicalUtf8))
                    {
                        throw new InvalidDataException(
                            $"Sunder RPC descriptors produced a conflicting canonical snapshot '{relativePath}'.");
                    }
                }
                else
                {
                    WriteStagedFile(stagingPath, relativePath, input.CanonicalUtf8);
                    snapshotContents.Add(relativePath, input.CanonicalUtf8);
                }
                snapshotPaths.Add(input.SourcePath, Path.Combine(generatedDirectory, relativePath));
            }
            var sourceSnapshotPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var contract in sourcePlan.OutputContracts.OrderBy(static contract => contract.ContractKey, StringComparer.Ordinal))
            {
                var relativePath = SnapshotRelativePath(contract.Descriptor);
                var canonicalUtf8 = contract.Descriptor.GetCanonicalUtf8();
                if (snapshotContents.TryGetValue(relativePath, out var existingBytes))
                {
                    if (!existingBytes.AsSpan().SequenceEqual(canonicalUtf8))
                    {
                        throw new InvalidDataException(
                            $"Sunder RPC source contracts produced a conflicting canonical snapshot '{relativePath}'.");
                    }
                }
                else
                {
                    WriteStagedFile(stagingPath, relativePath, canonicalUtf8);
                    snapshotContents.Add(relativePath, canonicalUtf8);
                }
                sourceSnapshotPaths.Add(contract.ContractKey, Path.Combine(generatedDirectory, relativePath));
            }

            WriteNewFile(stagingMarkerPath, Utf8NoBom.GetBytes(MarkerContents));
            completeSourcePlan.Inputs?.Verify();
            _ = ReadTree(stagingPath);
            GeneratedOutputPathSafety.EnsureRegularFile(
                stagingMarkerPath,
                "staged Sunder RPC generated-output marker");

            if (hasOwnedOutput
                && TreesAreByteIdentical(generatedDirectory, stagingPath)
                && HasValidOwnershipMarker(markerPath))
            {
                DeleteStagedDirectory(stagingPath);
                File.Delete(stagingMarkerPath);
            }
            else
            {
                GeneratedOutputPathSafety.EnsureRegularDirectory(
                    intermediateOutputPath,
                    "Sunder RPC intermediate output directory");
                GeneratedOutputTransaction.Commit(
                    new GeneratedOutputTransaction.StagedOutput(generatedDirectory, stagingPath),
                    new GeneratedOutputTransaction.StagedOutput(markerPath, stagingMarkerPath));
            }
            stagingPath = null;
            stagingMarkerPath = null;

            _ = ReadTree(generatedDirectory);
            if (!HasValidOwnershipMarker(markerPath))
            {
                throw new IOException($"Sunder RPC generated-output marker '{markerPath}' changed after publication.");
            }
            PublishTaskOutputs(
                plans,
                descriptorInputs.Values,
                snapshotPaths,
                sourcePlan,
                sourceSnapshotPaths,
                generatedDirectory,
                markerPath,
                immutableExportPath);
            return true;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: false);
            return false;
        }
        finally
        {
            TryDeleteStagedDirectory(stagingPath);
            TryDeleteStagedFile(stagingMarkerPath);
        }
    }

    private bool TryResolveConfiguration(
        out string projectDirectory,
        out string intermediateOutputPath,
        out string generatedDirectory,
        out string markerPath)
    {
        projectDirectory = string.Empty;
        intermediateOutputPath = string.Empty;
        generatedDirectory = string.Empty;
        markerPath = string.Empty;
        if (string.IsNullOrWhiteSpace(ProjectDirectory) || string.IsNullOrWhiteSpace(IntermediateOutputPath))
        {
            Log.LogError("Sunder RPC binding generation requires ProjectDirectory and IntermediateOutputPath.");
            return false;
        }

        try
        {
            projectDirectory = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(ProjectDirectory);
            intermediateOutputPath = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(
                Path.GetFullPath(IntermediateOutputPath, projectDirectory));
            GeneratedOutputPathSafety.EnsureRegularDirectory(projectDirectory, "Sunder RPC project directory");
            GeneratedOutputPathSafety.EnsureSafeAncestors(
                intermediateOutputPath,
                includeLeaf: true,
                "Sunder RPC intermediate output path");
            if (GeneratedOutputPathSafety.PathExists(intermediateOutputPath)
                && !Directory.Exists(intermediateOutputPath))
            {
                throw new IOException(
                    $"Sunder RPC IntermediateOutputPath '{IntermediateOutputPath}' must be a regular directory.");
            }
            if (Directory.Exists(intermediateOutputPath))
            {
                GeneratedOutputPathSafety.EnsureRegularDirectory(
                    intermediateOutputPath,
                    "Sunder RPC intermediate output directory");
            }

            generatedDirectory = Path.Combine(intermediateOutputPath, GeneratedDirectoryName);
            markerPath = GetOwnershipMarkerPath(generatedDirectory);
            GeneratedOutputPathSafety.EnsureSafeAncestors(
                generatedDirectory,
                includeLeaf: true,
                "Sunder RPC generated directory");
            GeneratedOutputPathSafety.EnsureSafeAncestors(
                markerPath,
                includeLeaf: true,
                "Sunder RPC generated-output marker");
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            Log.LogError($"Sunder RPC binding output configuration is invalid: {exception.Message}");
            return false;
        }
    }

    private bool TryResolveGenerationMode(out RpcGenerationMode mode)
    {
        mode = GenerationMode.Trim() switch
        {
            "Combined" => RpcGenerationMode.Combined,
            "ProjectExport" => RpcGenerationMode.ProjectExport,
            "Implementation" => RpcGenerationMode.Implementation,
            "ProjectImport" => RpcGenerationMode.ProjectImport,
            "EditorImports" => RpcGenerationMode.EditorImports,
            _ => RpcGenerationMode.Invalid,
        };
        if (mode != RpcGenerationMode.Invalid) return true;

        Log.LogError(
            $"Sunder RPC generation mode '{GenerationMode}' is invalid. "
            + "Allowed values are 'Combined', 'ProjectExport', 'Implementation', 'ProjectImport', and 'EditorImports'.");
        return false;
    }

    private static SourceAuthoringPlan SelectSourceOutputs(
        SourceAuthoringPlan complete,
        RpcGenerationMode mode)
    {
        if (mode == RpcGenerationMode.Combined) return complete;
        var scope = mode == RpcGenerationMode.ProjectExport
            ? SourceOutputScope.ProjectExport
            : SourceOutputScope.Implementation;
        return complete with
        {
            Files = complete.Files.Where(file => file.Scope == scope || file.RelativeOutputPath == SunderRpcAuthoringDocuments.ModelProjectionLogicalPath).ToArray(),
            Imports = mode == RpcGenerationMode.ProjectExport ? [] : complete.Imports,
            Projection = mode == RpcGenerationMode.ProjectExport ? null : complete.Projection,
        };
    }

    private ExpectedPackageProjectExport? ResolveExpectedPackageProjectExport(
        RpcGenerationMode mode,
        string projectDirectory,
        string generatedDirectory,
        SourceAuthoringPlan completeSourcePlan)
    {
        if (mode is not (RpcGenerationMode.Implementation or RpcGenerationMode.ProjectImport))
        {
            if (ExpectedPackageProjectExports.Length != 0)
            {
                Log.LogError("ExpectedPackageProjectExports is valid only in Implementation or ProjectImport mode.");
            }
            return null;
        }

        if (mode == RpcGenerationMode.Implementation)
        {
            var hasProjectExport = completeSourcePlan.Files.Any(static file =>
                file.Scope == SourceOutputScope.ProjectExport
                && string.Equals(
                    file.RelativeOutputPath,
                    SunderRpcAuthoringDocuments.PackageExportFileName,
                    StringComparison.Ordinal));
            if (!hasProjectExport)
            {
                if (ExpectedPackageProjectExports.Length != 0)
                {
                    Log.LogError(
                        "Implementation mode received a project export, but this project has no source-authored RPC export.");
                }
                return null;
            }
        }
        if (ExpectedPackageProjectExports.Length != 1)
        {
            Log.LogError(
                $"{mode} mode requires exactly one project-neutral RPC export; received {ExpectedPackageProjectExports.Length}.");
            return null;
        }

        try
        {
            var exportPath = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(
                ResolveTaskItemPath(ExpectedPackageProjectExports[0], projectDirectory));
            var expectedSha256 = ExpectedPackageProjectExports[0].GetMetadata("Sha256");
            if (expectedSha256.Length != 64
                || expectedSha256.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            {
                throw new InvalidDataException(
                    "The expected project export item does not carry a lowercase SHA-256 identity.");
            }
            var exportProjectIdentity = ExpectedPackageProjectExports[0].GetMetadata("ProjectIdentity");
            var exportConfiguration = ExpectedPackageProjectExports[0].GetMetadata("Configuration");
            var exportTargetFramework = ExpectedPackageProjectExports[0].GetMetadata("TargetFramework");
            var exportPackageId = ExpectedPackageProjectExports[0].GetMetadata("PackageId");
            var exportPackageVersion = ExpectedPackageProjectExports[0].GetMetadata("PackageVersion");
            if (string.IsNullOrWhiteSpace(exportProjectIdentity)
                || !string.Equals(exportConfiguration, Configuration.Trim(), StringComparison.Ordinal)
                || !Sunder.Sdk.Packaging.PackageId.TryParse(exportPackageId, out _)
                || !string.Equals(exportPackageVersion, PackageVersion.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The expected project export item has missing or mismatched project, configuration, target-framework, or package metadata.");
            }
            if (mode == RpcGenerationMode.Implementation
                && (!string.Equals(exportProjectIdentity, ProjectIdentity.Trim(), StringComparison.Ordinal)
                    || !string.Equals(exportTargetFramework, TargetFramework.Trim(), StringComparison.Ordinal)
                    || !string.Equals(exportPackageId, completeSourcePlan.PackageId, StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "The expected project export identity does not match this exact-RID implementation project.");
            }
            if (!string.Equals(
                    Path.GetFileName(exportPath),
                    SunderRpcAuthoringDocuments.PackageExportFileName,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The expected project export must be named '{SunderRpcAuthoringDocuments.PackageExportFileName}'.");
            }
            var exportGeneratedDirectory = Path.GetDirectoryName(exportPath)
                                           ?? throw new InvalidDataException(
                                               $"The expected project export '{exportPath}' has no generated directory.");
            if (!string.Equals(
                    Path.GetFileName(exportGeneratedDirectory),
                    GeneratedDirectoryName,
                    StringComparison.Ordinal)
                || PathComparer.Equals(exportGeneratedDirectory, generatedDirectory))
            {
                throw new InvalidDataException(
                    mode == RpcGenerationMode.Implementation
                        ? "Implementation mode requires a distinct project-neutral IntermediateOutputPath/sunder-rpc export directory. "
                          + "Runtime package implementation builds must be exact-RID nodes."
                        : "ProjectImport mode requires distinct producer and local IntermediateOutputPath/sunder-rpc directories.");
            }
            var exportIntermediateOutputPath = Path.GetDirectoryName(exportGeneratedDirectory)
                                               ?? throw new InvalidDataException(
                                                   $"The expected project export directory '{exportGeneratedDirectory}' has no parent.");
            var markerPath = GetOwnershipMarkerPath(exportGeneratedDirectory);
            GeneratedOutputPathSafety.EnsureSafeAncestors(
                exportGeneratedDirectory,
                includeLeaf: true,
                "Sunder RPC project export directory");
            GeneratedOutputPathSafety.EnsureSafeAncestors(
                markerPath,
                includeLeaf: true,
                "Sunder RPC project export marker");
            return new(
                exportPath,
                exportGeneratedDirectory,
                exportIntermediateOutputPath,
                markerPath,
                expectedSha256,
                exportProjectIdentity,
                exportConfiguration,
                exportTargetFramework,
                exportPackageId,
                exportPackageVersion,
                mode == RpcGenerationMode.Implementation ? completeSourcePlan.LockSha256 : null,
                mode == RpcGenerationMode.Implementation ? completeSourcePlan.Contracts : null)
            { RoleUses = mode == RpcGenerationMode.Implementation ? completeSourcePlan.Projection?.RoleUses ?? [] : [] };
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException
                                           or IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            Log.LogError($"The expected project-neutral RPC export is invalid: {exception.Message}");
            return null;
        }
    }

    private SunderRpcPackageProjectExport ValidateExpectedPackageProjectExport(ExpectedPackageProjectExport expected)
    {
        GeneratedOutputTransaction.RecoverAndCleanup(expected.GeneratedDirectory, expected.MarkerPath);
        GeneratedOutputPathSafety.EnsureRegularDirectory(
            expected.GeneratedDirectory,
            "Sunder RPC project export directory");
        GeneratedOutputPathSafety.EnsureRegularFile(expected.ExportPath, "Sunder RPC project export");
        if (!HasValidOwnershipMarker(expected.MarkerPath))
        {
            throw new InvalidDataException(
                $"The project-neutral RPC export directory '{expected.GeneratedDirectory}' has no valid ownership marker.");
        }
        var export = expected.Contracts is null
            ? SunderRpcAuthoringDocuments.ReadPackageProjectExport(
                expected.ExportPath,
                GetProducerVersion(),
                expected.ProjectIdentity,
                expected.Configuration,
                expected.TargetFramework,
                expected.PackageId, expected.PackageVersion)
            : ReadAndValidateImplementationExport(expected);
        if (!string.Equals(export.Sha256, expected.ExpectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The project-neutral RPC export changed after its producing target completed.");
        }
        return export;
    }

    private static SunderRpcPackageProjectExport ReadAndValidateImplementationExport(
        ExpectedPackageProjectExport expected)
    {
        var export = SunderRpcAuthoringDocuments.ReadPackageProjectExport(
            expected.ExportPath,
            GetProducerVersion(),
            expected.ProjectIdentity,
            expected.Configuration,
            expected.TargetFramework,
            expected.PackageId, expected.PackageVersion);
        if (!string.Equals(export.LockSha256, expected.LockSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exact-RID contract lock does not match the project-neutral RPC export.");
        }
        if (!export.RoleUses.Select(SunderRpcAuthoringDocuments.RoleUseIdentity).SequenceEqual(expected.RoleUses.Select(SunderRpcAuthoringDocuments.RoleUseIdentity), StringComparer.Ordinal))
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: The exact-RID source role uses do not match the project-neutral export.");
        var expectedByKey = expected.Contracts!.ToDictionary(
            static contract => contract.ContractKey,
            StringComparer.Ordinal);
        if (export.Contracts.Count != expectedByKey.Count
            || export.Contracts.Any(contract =>
                !expectedByKey.TryGetValue(contract.ContractKey, out var source)
                || source.Exposure != contract.Exposure
                || source.GeneratedLogicalName != contract.GeneratedLogicalName
                || !SunderRpcAuthoringDocuments.SameModelProjection(source.ModelProjection, contract.ModelProjection)
                || !SunderRpcAuthoringDocuments.SameMethodPolicies(source.MethodPolicies, contract.MethodPolicies)
                || !SunderRpcAuthoringDocuments.SameUsePolicy(source.AppUse, contract.AppUse)
                || !SunderRpcAuthoringDocuments.SameUsePolicy(source.RuntimeUse, contract.RuntimeUse)
                || !source.Descriptor.GetCanonicalUtf8().AsSpan()
                    .SequenceEqual(contract.Descriptor.GetCanonicalUtf8())
                || !source.Descriptor.GetCanonicalAbiProjectionUtf8().AsSpan()
                    .SequenceEqual(contract.Descriptor.GetCanonicalAbiProjectionUtf8())))
        {
            throw new InvalidDataException(
                "The exact-RID source contracts do not match the project-neutral RPC export.");
        }
        return export;
    }

    private List<BindingPlan>? BuildPlans(
        string projectDirectory,
        string generatedDirectory,
        IDictionary<string, SunderRpcDescriptorFileSnapshot> descriptorInputs)
    {
        var plans = new List<BindingPlan>(Bindings.Length);
        var outputOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in Bindings)
        {
            var namespaceName = binding.GetMetadata("Namespace").Trim();
            if (namespaceName.Length == 0)
            {
                Log.LogError($"SunderRpcBinding '{binding.ItemSpec}' requires 'Namespace' metadata.");
                continue;
            }
            if (!TryResolveAccessibility(binding, out var accessibility))
            {
                continue;
            }

            string descriptorPath;
            try
            {
                descriptorPath = Path.GetFullPath(binding.ItemSpec, projectDirectory);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Log.LogError($"SunderRpcBinding descriptor path '{binding.ItemSpec}' is invalid: {exception.Message}");
                continue;
            }

            if (!descriptorInputs.TryGetValue(descriptorPath, out var input))
            {
                try
                {
                    input = SunderRpcDescriptorFileReader.Read(
                        descriptorPath,
                        "SunderRpcBinding descriptor");
                    descriptorInputs.Add(descriptorPath, input);
                }
                catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or InvalidDataException
                                                  or SunderRpcDescriptorException)
                {
                    Log.LogError($"SunderRpcBinding descriptor '{binding.ItemSpec}' is invalid: {exception.Message}");
                    continue;
                }
            }

            string source;
            try
            {
                source = SunderRpcCSharpGenerator.Generate(input.Descriptor, namespaceName, accessibility);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                Log.LogError($"SunderRpcBinding '{binding.ItemSpec}' could not generate C# source: {exception.Message}");
                continue;
            }

            if (!TryResolveOutputPath(binding, input.Descriptor, generatedDirectory, out var relativeOutputPath, out var outputPath))
            {
                continue;
            }
            if (!outputOwners.TryAdd(outputPath, binding.ItemSpec))
            {
                Log.LogError(
                    $"SunderRpcBinding '{binding.ItemSpec}' and '{outputOwners[outputPath]}' resolve to the same output '{outputPath}'. "
                    + "Set distinct OutputFile metadata values.");
                continue;
            }
            plans.Add(new BindingPlan(
                binding.ItemSpec,
                relativeOutputPath,
                outputPath,
                Utf8NoBom.GetBytes(source)));
        }
        return Log.HasLoggedErrors ? null : plans;
    }

    private SourceAuthoringPlan BuildProjectImportPlan(
        string generatedDirectory,
        SunderRpcPackageProjectExport export)
    {
        ValidateSourceDocumentDimensions();
        var files = new List<SourceGeneratedFile>();
        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The compiler owns the complete projected source set, including shared
        // models. A second descriptor-only generator here would invent duplicate
        // public values and discard the export's source-derived model identity.
        foreach (var source in SunderRpcCSharpImportPrepass.PrepareOwnedPackageApi(export, PackageVersion.Trim()))
        {
            if (!TryNormalizeRelativeOutputFile(source.Path, out var normalized, out var error))
                throw new InvalidDataException($"rpc-authoring.evidence-mismatch: Project-import generated source '{source.Path}' is invalid. {error}");
            var relative = normalized.Replace(Path.DirectorySeparatorChar, '/');
            if (!sourcePaths.Add(relative))
                throw new InvalidDataException($"rpc-authoring.evidence-mismatch: Project-import generation repeats source path '{relative}'.");
            // A shared model/union file can declare several types. Membership is
            // the compiler's exact path set, not one guessed file per definition.
            AddSourceGeneratedFile(files, generatedDirectory, relative, Utf8NoBom.GetBytes(source.Text),
                compile: true, scope: SourceOutputScope.Implementation);
        }
        if (!sourcePaths.Contains("imports.g.cs"))
            AddSourceGeneratedFile(files, generatedDirectory, "imports.g.cs", Utf8NoBom.GetBytes("// <auto-generated />\n#nullable enable\n"),
                compile: true, scope: SourceOutputScope.Implementation);

        var inputFingerprint = SunderRpcAuthoringDocuments.ComputeProjectImportFingerprint(
            export.Sha256,
            ProjectIdentity.Trim(),
            Configuration.Trim(),
            TargetFramework.Trim());
        var receipt = SunderRpcAuthoringDocuments.WriteProjectImportReceipt(
            GetProducerVersion(), inputFingerprint, export.LockSha256, export.PackageId, PackageVersion.Trim(), export.Contracts);
        var projection = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(
            receipt, export.LockSha256, export.PackageId, PackageVersion.Trim());
        AddSourceGeneratedFile(
            files,
            generatedDirectory,
            SunderRpcAuthoringDocuments.AuthoringReceiptFileName,
            receipt,
            compile: false,
            scope: SourceOutputScope.Implementation);
        AddSourceGeneratedFile(files, generatedDirectory, SunderRpcAuthoringDocuments.ModelProjectionLogicalPath,
            SunderRpcAuthoringDocuments.PublicModelProjectionBytes(projection), false, SourceOutputScope.Implementation);
        return new(files, [], export.PackageId, export.LockSha256, export.Contracts, [], projection);
    }

    private SourceAuthoringPlan BuildEditorImportPlan(string projectDirectory, string generatedDirectory)
    {
        var lockPath = ResolveProjectFile(projectDirectory, ContractLockPath, SunderRpcAuthoringDocuments.ContractLockFileName);
        var contractLock = GeneratedOutputPathSafety.PathExists(lockPath)
            ? SunderRpcAuthoringDocuments.ReadContractLock(lockPath, AllowDevelopmentRegistryOrigin)
            : SunderRpcAuthoringDocuments.ParseContractLock("{\n  \"lockVersion\": 1,\n  \"packageArtifacts\": {},\n  \"packageImports\": {},\n  \"providerImports\": {},\n  \"platformImports\": {}\n}\n"u8.ToArray());
        var imports = SunderRpcCSharpImportPrepass.Prepare(contractLock,
            string.IsNullOrWhiteSpace(ContractCacheRoot) ? SunderRpcContractCache.DefaultRoot : Path.GetFullPath(ContractCacheRoot, projectDirectory));
        var files = new List<SourceGeneratedFile>();
        foreach (var source in imports.Sources)
            AddSourceGeneratedFile(files, generatedDirectory, source.Path, Utf8NoBom.GetBytes(source.Text), true, SourceOutputScope.Implementation);
        AddSourceGeneratedFile(files, generatedDirectory, "design-time-only.json", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, purpose = "editor-imports-only", artifactEligible = false,
            projectIdentity = ProjectIdentity.Trim(), configuration = Configuration.Trim(), targetFramework = TargetFramework.Trim(),
            lockSha256 = contractLock.Sha256,
            generatedSources = files.OrderBy(file => file.RelativeOutputPath, StringComparer.Ordinal).Select(file => new
                { path = file.RelativeOutputPath, sha256 = Convert.ToHexStringLower(SHA256.HashData(file.Contents)) }).ToArray(),
        }), false, SourceOutputScope.Implementation);
        return new(files, [], PackageId.Trim(), contractLock.Sha256, [], [], null);
    }

    private SourceAuthoringPlan? BuildSourceAuthoringPlan(
        string projectDirectory,
        string generatedDirectory,
        SunderRpcOwnedPackageBaseline? baselineOverride = null,
        bool allowBootstrap = false,
        bool validateBaseline = true,
        bool forceAuthoring = false,
        SunderRpcInputSnapshot? inputSnapshot = null,
        IReadOnlySet<string>? allowUnboundImportedProviderKeys = null,
        Action<SunderRpcCSharpCompilationResult>? validateSelection = null)
    {
        inputSnapshot ??= new SunderRpcInputSnapshot();
        var sources = ReadSourceFiles(projectDirectory, inputSnapshot);
        if (sources is null) return null;
        var ownedContractsPath = ResolveProjectFile(
            projectDirectory,
            OwnedContractsPath,
            SunderRpcAuthoringDocuments.OwnedContractsFileName);
        var contractLockPath = ResolveProjectFile(
            projectDirectory,
            ContractLockPath,
            SunderRpcAuthoringDocuments.ContractLockFileName);
        if (!forceAuthoring && !HasSourceAuthoredServiceAttribute(sources, DefineConstants)
            && !GeneratedOutputPathSafety.PathExists(ownedContractsPath)
            && !GeneratedOutputPathSafety.PathExists(contractLockPath))
        {
            return SourceAuthoringPlan.Empty;
        }

        var baselineExists = GeneratedOutputPathSafety.PathExists(ownedContractsPath);
        var baseline = baselineOverride ?? (baselineExists ? SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(ownedContractsPath) : null);
        if (baselineExists) inputSnapshot?.Record(ownedContractsPath, SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(ownedContractsPath).ExactBytes);
        else inputSnapshot?.ExpectAbsent(ownedContractsPath);
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(contractLockPath, AllowDevelopmentRegistryOrigin);
        inputSnapshot?.Record(contractLockPath, contractLock.ExactBytes);
        var imports = SunderRpcCSharpImportPrepass.Prepare(contractLock,
            string.IsNullOrWhiteSpace(ContractCacheRoot) ? SunderRpcContractCache.DefaultRoot : Path.GetFullPath(ContractCacheRoot, projectDirectory));
        ValidateSourceDocumentDimensions();
        var references = ResolveReferencePaths(projectDirectory);
        if (references is null) return null;
        if (inputSnapshot is not null)
            foreach (var reference in references) inputSnapshot.Read(reference, int.MaxValue, "C# RPC compilation reference");
        var defineConstants = DefineConstants
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var acceptedInput = new SunderRpcCSharpCompilerInput(
            baseline?.PackageId ?? PackageId.Trim(),
            PackageVersion.Trim(),
            sources,
            references,
            defineConstants,
            NullableEnabled,
            OwnedContractsByContractKey: baseline is null ? null : SunderRpcAuthoringDocuments.CompilerIdentities(baseline),
            SupplementalSources: imports.Sources,
            CompilationOutputKind: GeneratePackageRoles ? SunderRpcCSharpCompilationOutputKind.Library : ResolveCompilationOutputKind(),
            ImportedProviderSurfaces: imports.ProviderSurfaces,
            AllowUnboundImportedProviderKeys: allowUnboundImportedProviderKeys,
            AssemblyName: AssemblyName,
            GenerateServiceInterfaces: GeneratePackageRoles,
            ImportedPackageApis: imports.PackageApis);
        var compiler = new SunderRpcCSharpSourceCompiler();
        var resourceInputs = GeneratePackageRoles ? Sunder.Package.Build.Authoring.SunderPackageResourcePlanner.Read(projectDirectory,
            ResourceItems.GroupBy(item => item.GetMetadata("ResourceKind"), StringComparer.Ordinal).Select(group => (group.Key, group.ToArray())).ToArray()) : [];
        foreach (var resource in resourceInputs) inputSnapshot?.Record(resource.SourcePath, resource.Contents);
        var generateXamlMembers = resourceInputs.Any(resource => resource.Kind == "AvaloniaXaml")
            && !GeneratorProperties.Any(property => property.ItemSpec == "AvaloniaNameGeneratorIsEnabled" && property.GetMetadata("Value").Equals("false", StringComparison.OrdinalIgnoreCase));
        var avaloniaGenerator = generateXamlMembers
            ? GeneratorAnalyzers.SingleOrDefault(item => Path.GetFileName(item.ItemSpec) == "Avalonia.Generators.dll")?.ItemSpec : null;
        if (generateXamlMembers && avaloniaGenerator is null)
            throw new InvalidDataException("package-authoring.xaml-generator-missing: Resolve the Avalonia package generator before single-project analysis.");
        if (avaloniaGenerator is not null) inputSnapshot?.Read(avaloniaGenerator, 64 * 1024 * 1024, "Avalonia generator");
        using var frameworkGenerator = avaloniaGenerator is null ? null : new Sunder.Package.Build.Authoring.SunderAvaloniaSourceGenerator(avaloniaGenerator, resourceInputs, GeneratorProperties);
        var regexPath = GeneratePackageRoles ? GeneratorAnalyzers.SingleOrDefault(item => Path.GetFileName(item.ItemSpec)
            == Sunder.Package.Build.Authoring.SunderFrameworkSourceGenerator.RegexAssemblyFileName)?.ItemSpec : null;
        if (regexPath is not null) inputSnapshot?.Read(regexPath, 64 * 1024 * 1024, "SDK regex generator");
        using var regexGenerator = regexPath is null ? null : new Sunder.Package.Build.Authoring.SunderFrameworkSourceGenerator(regexPath, GeneratorProperties);
        var mvvmPath = GeneratePackageRoles ? GeneratorAnalyzers.SingleOrDefault(item => Path.GetFileName(item.ItemSpec)
            == Sunder.Package.Build.Authoring.SunderFrameworkSourceGenerator.MvvmAssemblyFileName)?.ItemSpec : null;
        if (mvvmPath is not null) inputSnapshot?.Read(mvvmPath, 64 * 1024 * 1024, "MVVM Toolkit generator");
        using var mvvmGenerator = mvvmPath is null ? null : new Sunder.Package.Build.Authoring.SunderFrameworkSourceGenerator(mvvmPath, GeneratorProperties);
        var roleGenerators = new[] { regexGenerator, mvvmGenerator }.OfType<Sunder.Package.Build.Authoring.SunderFrameworkSourceGenerator>().ToArray();
        var ownedClients = SunderRpcCSharpOwnedClientPrepass.Compile(compiler, acceptedInput,
            frameworkGenerator is null && roleGenerators.Length == 0 ? null : compilation =>
                (frameworkGenerator?.Generate(compilation) ?? []).Concat(roleGenerators.SelectMany(generator => generator.Generate(compilation))).ToArray());
        var accepted = ownedClients.Compilation;
        if (!accepted.Success)
        {
            LogCompilerDiagnostics(accepted.Diagnostics);
            return null;
        }
        if (baseline is null && !allowBootstrap) VerifyNoLostPublicBaseline(projectDirectory, generatedDirectory);
        baseline ??= SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(
            SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(new(
                accepted.ResolvedPackageId,
                new Dictionary<string, SunderRpcOwnedContractBaseline>(StringComparer.Ordinal), [], string.Empty)));
        if (!string.IsNullOrWhiteSpace(PackageId) && PackageId.Trim() != baseline.PackageId)
        {
            throw new InvalidDataException("Source package identity does not match the owned baseline.");
        }
        if (validateBaseline) SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, accepted.Contracts);

        var files = new List<SourceGeneratedFile>();
        foreach (var source in imports.Sources)
        {
            var text = contractLock.PackageArtifacts.Count == 0 && contractLock.PlatformImports.Count == 0 && source.Path == "imports.g.cs"
                ? "// <auto-generated />\n#nullable enable\n"
                : source.Text;
            AddSourceGeneratedFile(files, generatedDirectory, source.Path, Utf8NoBom.GetBytes(text),
                compile: true, scope: SourceOutputScope.Implementation);
        }
        foreach (var source in ownedClients.Sources)
        {
            AddSourceGeneratedFile(
                files,
                generatedDirectory,
                source.Path,
                Utf8NoBom.GetBytes(source.Text),
                compile: true,
                scope: SourceOutputScope.Implementation);
        }
        var authoringSource = Utf8NoBom.GetBytes(SunderRpcCSharpImplementationEmitter.Emit(accepted.Contracts, accepted.ImportedProviders));
        AddSourceGeneratedFile(
            files,
            generatedDirectory,
            "authoring.g.cs",
            authoringSource,
            compile: true,
            scope: SourceOutputScope.Implementation);

        var generatedSources = files
            .Where(static file => file.Compile)
            .Select(file => new SunderRpcCSharpSourceFile(file.OutputPath, StrictUtf8.GetString(file.Contents)))
            .ToArray();
        var verified = compiler.Compile(acceptedInput with
        {
            SupplementalSources = generatedSources.Concat(ownedClients.FrameworkSources).ToArray(),
            RequireGeneratedMembersResolved = true,
        });
        if (!verified.Success)
        {
            LogCompilerDiagnostics(verified.Diagnostics);
            return null;
        }
        if (validateBaseline) SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, verified.Contracts);
        if (accepted.Contracts.Count != verified.Contracts.Count || accepted.Contracts.Any(contract =>
            verified.Contracts.SingleOrDefault(value => value.ContractKey == contract.ContractKey) is not { } final
            || !SunderRpcAuthoringDocuments.SameModelProjection(contract.ModelProjection, final.ModelProjection)
            || !SunderRpcAuthoringDocuments.SameMethodPolicies(contract.MethodPolicies, final.MethodPolicies)))
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: Source model projection changed after resolving generated members.");
        if (!authoringSource.AsSpan().SequenceEqual(
                Utf8NoBom.GetBytes(SunderRpcCSharpImplementationEmitter.Emit(verified.Contracts, verified.ImportedProviders))))
        {
            throw new InvalidDataException(
                "rpc-authoring.evidence-mismatch: Regenerated authoring.g.cs changed after resolving generated members.");
        }

        IReadOnlySet<string> presentRoles = new HashSet<string>(StringComparer.Ordinal) { "runtime" };
        if (GeneratePackageRoles)
        {
            var compilation = verified.SourceCompilation!;
            var ordinary = sources.Select(source => source.Path).ToHashSet(StringComparer.Ordinal);
            var markup = Sunder.Package.Build.Authoring.SunderPackageResourcePlanner.Markup(compilation, resourceInputs);
            var roles = Sunder.Package.Build.Authoring.SunderPackageRolePlanner.Plan(compilation,
                compilation.SyntaxTrees.Where(tree => ordinary.Contains(tree.FilePath)).ToArray(), verified.Contracts.SelectMany(contract => contract.SourceTypes).Select(value => value.SourceType), markup.Types,
                verified.TrustedImportedModelTypes);
            if (!roles.Success) { LogCompilerDiagnostics(roles.Diagnostics); return null; }
            if (roles.Views.Count != 0 || roles.Types.Any(type => type.Roles == Sunder.Package.Build.Authoring.SunderPackageRoleMembership.App))
                presentRoles = new HashSet<string>(StringComparer.Ordinal) { "app", "runtime" };
        }
        foreach (var import in contractLock.PackageImports.Values)
            if (!import.ConsumerRoles.Any(presentRoles.Contains)) throw new InvalidDataException($"package-api.consumer-roles-unavailable: '{import.PackageId}' has no matching role in this canonical project; add a matching role or explicitly review consumerRoles.");

        SunderRpcAuthoringDocuments.ValidateImportedProviderReferenceUses(contractLock, verified);
        if (validateSelection is not null)
        {
            if (!SelectionProviders(accepted).SequenceEqual(SelectionProviders(verified), StringComparer.Ordinal))
                throw new InvalidDataException("rpc-authoring.evidence-mismatch: Proposed provider metadata changed after resolving generated source.");
            validateSelection(verified);
            return SourceAuthoringPlan.Empty;
        }

        var producerVersion = GetProducerVersion();
        var inputFingerprint = SunderRpcAuthoringDocuments.ComputeInputFingerprint(
            baseline.PackageId,
            PackageVersion.Trim(),
            ProjectIdentity.Trim(),
            Configuration.Trim(),
            TargetFramework.Trim(),
            NullableEnabled,
            defineConstants,
            sources,
            references,
            baseline,
            contractLock,
            AssemblyName,
            projectDirectory,
            inputSnapshot, verified.ImportedUses.Select(use => use.Evidence).ToArray());
        var exportBytes = SunderRpcAuthoringDocuments.WritePackageExport(
            producerVersion,
            ProjectIdentity.Trim(),
            Configuration.Trim(),
            TargetFramework.Trim(),
            inputFingerprint,
            contractLock.Sha256,
            baseline.PackageId,
            PackageVersion.Trim(),
            accepted.Contracts, accepted.ImportedUses.Select(use => use.Evidence).ToArray());
        var verifiedExport = SunderRpcAuthoringDocuments.WritePackageExport(producerVersion, ProjectIdentity.Trim(), Configuration.Trim(),
            TargetFramework.Trim(), inputFingerprint, contractLock.Sha256, baseline.PackageId, PackageVersion.Trim(), verified.Contracts, verified.ImportedUses.Select(use => use.Evidence).ToArray());
        if (!exportBytes.AsSpan().SequenceEqual(verifiedExport))
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: The project export or consumer policy changed after resolving generated members.");
        AddSourceGeneratedFile(
            files,
            generatedDirectory,
            SunderRpcAuthoringDocuments.PackageExportFileName,
            exportBytes,
            compile: false,
            scope: SourceOutputScope.ProjectExport);
        var receipt = SunderRpcAuthoringDocuments.WriteAuthoringReceipt(
            producerVersion, inputFingerprint, contractLock, baseline.PackageId, PackageVersion.Trim(), verified, imports, presentRoles: presentRoles);
        // Registration source does not encode every receipt field, including provider audience.
        var acceptedReceipt = SunderRpcAuthoringDocuments.WriteAuthoringReceipt(
            producerVersion, inputFingerprint, contractLock, baseline.PackageId, PackageVersion.Trim(), accepted, imports, presentRoles: presentRoles);
        if (!acceptedReceipt.AsSpan().SequenceEqual(receipt))
        {
            throw new InvalidDataException(
                "rpc-authoring.evidence-mismatch: The complete authoring receipt changed after resolving generated members.");
        }
        var projection = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(
            receipt, contractLock.Sha256, baseline.PackageId, PackageVersion.Trim());
        AddSourceGeneratedFile(
            files,
            generatedDirectory,
            SunderRpcAuthoringDocuments.AuthoringReceiptFileName,
            receipt,
            compile: false,
            scope: SourceOutputScope.Implementation);
        if (GeneratePackageRoles && !AddPackageRoleSources(files, generatedDirectory, projectDirectory, verified, sources, ownedClients.Sources, inputFingerprint,
                resourceInputs, ownedClients.FrameworkSources.Where(source => !Sunder.Package.Build.Authoring.SunderFrameworkSourceGenerator.IsRoleGenerated(source.Path)).ToArray(),
                new[] { frameworkGenerator?.AssemblyInput }.OfType<Sunder.Package.Build.Authoring.SunderPackageFileEvidence>()
                    .Concat(roleGenerators.Select(generator => generator.AssemblyInput)).ToArray(), roleGenerators, contractLock, imports, presentRoles, inputSnapshot!))
            return null;

        if (presentRoles.Contains("app"))
        {
            var appReceipt = files.Single(file => file.RelativeOutputPath == SunderRpcAuthoringDocuments.AppAuthoringReceiptFileName);
            var appProjection = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(appReceipt.Contents, contractLock.Sha256, baseline.PackageId, PackageVersion.Trim());
            projection = SunderRpcAuthoringDocuments.MergeReceiptProjections([projection, appProjection]);
        }

        AddSourceGeneratedFile(files, generatedDirectory, SunderRpcAuthoringDocuments.ModelProjectionLogicalPath,
            SunderRpcAuthoringDocuments.PublicModelProjectionBytes(projection), false, SourceOutputScope.Implementation);
        inputSnapshot?.Verify();
        return new(files, accepted.Contracts, baseline.PackageId, contractLock.Sha256, [], imports.Contracts, projection)
        {
            Baseline = baseline,
            Inputs = inputSnapshot,
        };
    }

    internal IReadOnlyList<SunderRpcCSharpSourceFile>? ReadSourceFiles(string projectDirectory, SunderRpcInputSnapshot? inputSnapshot = null)
    {
        inputSnapshot?.CaptureSourceMembership(projectDirectory, () => SourceFiles
            .Where(item => !string.Equals(item.GetMetadata("AutoGen"), "true", StringComparison.OrdinalIgnoreCase))
            .Select(item => ResolveTaskItemPath(item, projectDirectory)), [IntermediateOutputPath, ProjectExtensionsPath]);
        foreach (var path in ProjectImports.Select(item => ResolveTaskItemPath(item, projectDirectory))
                     .Concat(string.IsNullOrWhiteSpace(ProjectFile) ? [] : new[] { Path.GetFullPath(ProjectFile, projectDirectory) }).Distinct(PathComparer))
            inputSnapshot?.Read(path, 16_777_216, "evaluated RPC project/import input");
        var result = new List<SunderRpcCSharpSourceFile>(SourceFiles.Length);
        var seen = new HashSet<string>(PathComparer);
        foreach (var item in SourceFiles)
        {
            if (string.Equals(item.GetMetadata("AutoGen"), "true", StringComparison.OrdinalIgnoreCase)) continue;
            string path;
            byte[] bytes;
            try
            {
                path = ResolveTaskItemPath(item, projectDirectory);
                if (!seen.Add(path)) continue;
                bytes = SunderRpcDescriptorFileReader.ReadStableBytes(
                    path,
                    4_194_304,
                    "C# RPC source file");
                inputSnapshot?.Record(path, bytes);
            }
            catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or PathTooLongException
                                              or IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException)
            {
                Log.LogError($"C# RPC source input '{item.ItemSpec}' is invalid: {exception.Message}");
                continue;
            }

            try
            {
                var start = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
                result.Add(new(path, StrictUtf8.GetString(bytes, start, bytes.Length - start)));
            }
            catch (DecoderFallbackException exception)
            {
                Log.LogError($"C# RPC source input '{item.ItemSpec}' is not UTF-8: {exception.Message}");
            }
        }
        return Log.HasLoggedErrors ? null : result;
    }

    internal static bool HasSourceAuthoredServiceAttribute(IEnumerable<SunderRpcCSharpSourceFile> sources, string defineConstants)
    {
        var options = new CSharpParseOptions(preprocessorSymbols: defineConstants.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var roots = sources.Select(source => CSharpSyntaxTree.ParseText(source.Text, options).GetRoot()).ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "SunderService", "SunderServiceAttribute", "SunderServiceProvider", "SunderServiceProviderAttribute",
        };
        foreach (var alias in roots.SelectMany(static root => root.DescendantNodes().OfType<UsingDirectiveSyntax>()))
        {
            if (alias.Alias is not null && names.Contains(Identifier(alias.Name)))
            {
                var name = alias.Alias.Name.Identifier.ValueText;
                names.Add(name);
                if (name.EndsWith("Attribute", StringComparison.Ordinal)) names.Add(name[..^"Attribute".Length]);
            }
        }
        foreach (var root in roots)
        {
            foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
            {
                if (names.Contains(Identifier(attribute.Name)))
                {
                    return true;
                }
            }
        }
        return false;

        static string Identifier(NameSyntax? syntax) => syntax switch
        {
            IdentifierNameSyntax name => name.Identifier.ValueText,
            QualifiedNameSyntax name => name.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax name => name.Name.Identifier.ValueText,
            _ => string.Empty,
        };
    }

    private IReadOnlyList<string>? ResolveReferencePaths(string projectDirectory)
    {
        var result = new List<string>(ReferencePaths.Length);
        var seen = new HashSet<string>(PathComparer);
        foreach (var item in ReferencePaths)
        {
            try
            {
                var path = ResolveTaskItemPath(item, projectDirectory);
                if (seen.Add(path)) result.Add(path);
            }
            catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or PathTooLongException)
            {
                Log.LogError($"C# RPC reference '{item.ItemSpec}' is invalid: {exception.Message}");
            }
        }
        return Log.HasLoggedErrors ? null : result;
    }

    private void ValidateSourceDocumentDimensions()
    {
        if (!SemanticVersion.TryParse(PackageVersion.Trim(), out _)
            || !IsPortableBuildValue(ProjectIdentity, 128, allowPlus: false)
            || !IsPortableBuildValue(Configuration, 64, allowPlus: true)
            || !IsPortableBuildValue(TargetFramework, 64, allowPlus: true))
        {
            throw new InvalidDataException(
                "Source-authored RPC requires a strict package version and portable project identity, configuration, and target framework.");
        }
    }

    private SunderRpcCSharpCompilationOutputKind ResolveCompilationOutputKind()
        => OutputType.Trim() switch
        {
            "" or "Library" => SunderRpcCSharpCompilationOutputKind.Library,
            "Exe" => SunderRpcCSharpCompilationOutputKind.ConsoleApplication,
            "WinExe" => SunderRpcCSharpCompilationOutputKind.WindowsApplication,
            _ => throw new InvalidDataException(
                $"Source-authored RPC does not support project output type '{OutputType}'."),
        };

    private static bool IsPortableBuildValue(string value, int maximumLength, bool allowPlus)
    {
        value = value.Trim();
        if (value.Length is < 1 || value.Length > maximumLength || !char.IsAsciiLetterOrDigit(value[0])
            || !char.IsAsciiLetterOrDigit(value[^1]))
        {
            return false;
        }
        return value.All(character => char.IsAsciiLetterOrDigit(character)
                                      || character is '.' or '_' or '-'
                                      || allowPlus && character == '+');
    }

    private void LogCompilerDiagnostics(IEnumerable<SunderRpcCSharpCompilerDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            var location = diagnostic.Path is null
                ? string.Empty
                : $"{diagnostic.Path}({diagnostic.Line},{diagnostic.Column})";
            Log.LogError($"{diagnostic.Id}: {location}: {diagnostic.Message}");
            foreach (var related in diagnostic.RelatedSpans ?? [])
            {
                Log.LogMessage(
                    MessageImportance.Low,
                    $"{diagnostic.Id} related location: {related.Path}({related.StartLine},{related.StartColumn}).");
            }
        }
    }

    private static string ResolveProjectFile(string projectDirectory, string configuredPath, string defaultFileName)
        => Path.GetFullPath(
            string.IsNullOrWhiteSpace(configuredPath) ? defaultFileName : configuredPath,
            projectDirectory);

    private static string ResolveTaskItemPath(ITaskItem item, string projectDirectory)
    {
        var fullPath = item.GetMetadata("FullPath");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(fullPath) ? item.ItemSpec : fullPath, projectDirectory);
    }

    private static void AddSourceGeneratedFile(
        ICollection<SourceGeneratedFile> files,
        string generatedDirectory,
        string relativeOutputPath,
        byte[] contents,
        bool compile,
        SourceOutputScope scope)
        => files.Add(new(
            relativeOutputPath,
            Path.GetFullPath(Path.Combine(generatedDirectory, relativeOutputPath)),
            contents,
            compile,
            scope));

    private static string GetProducerVersion()
    {
        var assembly = typeof(GenerateSunderRpcBindingsTask).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (SemanticVersion.TryParse(informationalVersion, out _)) return informationalVersion!;
        var version = assembly.GetName().Version;
        var fallback = version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        return SemanticVersion.TryParse(fallback, out _) ? fallback : "1.0.0";
    }

    private bool TryResolveAccessibility(
        ITaskItem binding,
        out SunderRpcCSharpAccessibility accessibility)
    {
        var value = binding.GetMetadata("Accessibility");
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "Public", StringComparison.Ordinal))
        {
            accessibility = SunderRpcCSharpAccessibility.Public;
            return true;
        }
        if (string.Equals(value, "Internal", StringComparison.Ordinal))
        {
            accessibility = SunderRpcCSharpAccessibility.Internal;
            return true;
        }

        accessibility = default;
        Log.LogError(
            $"SunderRpcBinding '{binding.ItemSpec}' Accessibility metadata '{value}' is invalid. "
            + "Allowed values are 'Public' and 'Internal'.");
        return false;
    }

    private bool TryResolveOutputPath(
        ITaskItem binding,
        SunderRpcContractDescriptor descriptor,
        string generatedDirectory,
        out string relativeOutputPath,
        out string outputPath)
    {
        relativeOutputPath = string.Empty;
        outputPath = string.Empty;
        var authoredOutputFile = binding.GetMetadata("OutputFile").Trim();
        var requestedOutputFile = authoredOutputFile.Length == 0
            ? DefaultOutputFile(descriptor)
            : authoredOutputFile;
        if (!TryNormalizeRelativeOutputFile(requestedOutputFile, out relativeOutputPath, out var error))
        {
            Log.LogError($"SunderRpcBinding '{binding.ItemSpec}' OutputFile '{authoredOutputFile}' is invalid: {error}");
            return false;
        }

        try
        {
            outputPath = Path.GetFullPath(Path.Combine(generatedDirectory, relativeOutputPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Log.LogError($"SunderRpcBinding '{binding.ItemSpec}' OutputFile '{authoredOutputFile}' is invalid: {exception.Message}");
            return false;
        }
        if (!GeneratedOutputPathSafety.IsDescendantOf(outputPath, generatedDirectory))
        {
            Log.LogError(
                $"SunderRpcBinding '{binding.ItemSpec}' OutputFile '{authoredOutputFile}' must remain under the owned generated directory.");
            return false;
        }
        return true;
    }

    private bool TryGetOwnedOutputState(string generatedDirectory, string markerPath, out bool hasOwnedOutput)
    {
        hasOwnedOutput = false;
        var outputExists = GeneratedOutputPathSafety.PathExists(generatedDirectory);
        var markerExists = GeneratedOutputPathSafety.PathExists(markerPath);
        if (outputExists)
        {
            try
            {
                GeneratedOutputPathSafety.EnsureRegularDirectory(
                    generatedDirectory,
                    "Sunder RPC generated directory");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.LogError($"Sunder RPC generated directory '{generatedDirectory}' is unsafe: {exception.Message}");
                return false;
            }
            if (!markerExists || !HasValidOwnershipMarker(markerPath))
            {
                Log.LogError(
                    $"Sunder RPC generated directory '{generatedDirectory}' already exists without its valid generated-output marker and will not be replaced.");
                return false;
            }
            hasOwnedOutput = true;
            return true;
        }
        if (markerExists)
        {
            try
            {
                GeneratedOutputPathSafety.EnsureRegularFile(
                    markerPath,
                    "Sunder RPC generated-output marker");
                if (!HasValidOwnershipMarker(markerPath))
                {
                    Log.LogError(
                        $"Sunder RPC generated-output marker '{markerPath}' exists without its generated directory and is invalid.");
                    return false;
                }
                File.Delete(markerPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.LogError(
                    $"Sunder RPC generated-output marker '{markerPath}' could not be recovered: {exception.Message}");
                return false;
            }
        }
        return true;
    }

    private void PublishTaskOutputs(
        IReadOnlyList<BindingPlan> plans,
        IEnumerable<SunderRpcDescriptorFileSnapshot> descriptorInputs,
        IReadOnlyDictionary<string, string> snapshotPaths,
        SourceAuthoringPlan sourcePlan,
        IReadOnlyDictionary<string, string> sourceSnapshotPaths,
        string generatedDirectory,
        string markerPath,
        string? immutableExportPath)
    {
        var compileItems = new List<ITaskItem>(plans.Count + sourcePlan.Files.Count(static file => file.Compile));
        foreach (var plan in plans.OrderBy(static plan => plan.OutputPath, StringComparer.Ordinal))
        {
            compileItems.Add(GeneratedCompileItem(plan.OutputPath));
        }
        foreach (var file in sourcePlan.Files
                     .Where(static file => file.Compile)
                     .OrderBy(static file => file.OutputPath, StringComparer.Ordinal))
        {
            compileItems.Add(GeneratedCompileItem(file.OutputPath));
        }

        var snapshots = new List<ITaskItem>();
        foreach (var input in descriptorInputs.OrderBy(static input => input.SourcePath, PathComparer))
        {
            var descriptor = input.Descriptor;
            snapshots.Add(SnapshotItem(
                snapshotPaths[input.SourcePath],
                input.SourcePath,
                descriptor,
                generatedDirectory,
                markerPath));
        }
        foreach (var contract in sourcePlan.OutputContracts.OrderBy(static contract => contract.ContractKey, StringComparer.Ordinal))
        {
            var snapshotPath = sourceSnapshotPaths[contract.ContractKey];
            snapshots.Add(SnapshotItem(
                snapshotPath,
                snapshotPath,
                contract.Descriptor,
                generatedDirectory,
                markerPath));
        }

        CompileItems = compileItems.ToArray();
        if (GenerationMode == "EditorImports")
        {
            var snapshot = PublishImmutableGeneratedPlan(sourcePlan, generatedDirectory, "design-time-only.json", "sunder-editor-snapshots",
                ".sunder-editor-cache", "sunder.editor-cache.v1\n");
            CompileItems = compileItems.Select(item => GeneratedCompileItem(Path.Combine(snapshot, Path.GetRelativePath(generatedDirectory, item.ItemSpec)))).ToArray();
        }
        PopulatePackageRoleOutputs(sourcePlan);
        BindingSnapshots = snapshots.ToArray();
        ContractBundles = sourcePlan.OutputContracts
            .OrderBy(static contract => contract.ContractKey, StringComparer.Ordinal)
            .Select(contract =>
            {
                var item = new TaskItem(sourceSnapshotPaths[contract.ContractKey]);
                item.SetMetadata("ContractId", contract.Descriptor.ContractId);
                item.SetMetadata("Version", contract.Descriptor.Version);
                item.SetMetadata("Exposure", contract.Exposure == SunderServiceExposure.Package ? "package" : "public");
                item.SetMetadata("DescriptorPath", $"contracts/{contract.ContractKey}.rpc.json");
                return (ITaskItem)item;
            })
            .ToArray();
        RpcProviders = (sourcePlan.Projection?.Manifest.Provides ?? [])
            .OrderBy(static item => item!.ProviderId, StringComparer.Ordinal)
            .Select(item =>
            {
                var provider = new TaskItem(item!.ProviderId!);
                provider.SetMetadata("ContractId", item.ContractId!);
                provider.SetMetadata("ContractVersion", item.ContractVersion!);
                provider.SetMetadata("Role", item.Role!);
                provider.SetMetadata("Audience", item.Audience!);
                return (ITaskItem)provider;
            })
            .ToArray();
        RpcUses = (sourcePlan.Projection?.Manifest.UsesContracts ?? [])
            .OrderBy(static use => use!.ContractId, StringComparer.Ordinal)
            .Select(item =>
            {
                var use = new TaskItem(item!.ContractId!);
                use.SetMetadata("ContractVersion", item.ContractVersion!);
                use.SetMetadata("Role", item.Role!);
                use.SetMetadata("Required", item.Required == true ? "true" : "false");
                use.SetMetadata("Actions", string.Join(';', item.Actions!));
                return (ITaskItem)use;
            })
            .ToArray();
        PackageProjectExports = sourcePlan.Files
            .Where(static file => string.Equals(
                file.RelativeOutputPath,
                SunderRpcAuthoringDocuments.PackageExportFileName,
                StringComparison.Ordinal))
            .Select(file =>
            {
                var item = new TaskItem(immutableExportPath ?? file.OutputPath);
                item.SetMetadata(
                    "Sha256",
                    Convert.ToHexString(SHA256.HashData(file.Contents)).ToLowerInvariant());
                item.SetMetadata("ProjectIdentity", ProjectIdentity.Trim());
                item.SetMetadata("Configuration", Configuration.Trim());
                item.SetMetadata("TargetFramework", TargetFramework.Trim());
                item.SetMetadata("PackageId", sourcePlan.PackageId);
                item.SetMetadata("PackageVersion", PackageVersion.Trim());
                var exportDirectory = Path.GetDirectoryName(item.ItemSpec)!;
                item.SetMetadata("GeneratedDirectory", exportDirectory);
                item.SetMetadata("IntermediateOutputPath", Path.GetDirectoryName(exportDirectory)!);
                return (ITaskItem)item;
            })
            .ToArray();
        AuthoringReceipts = sourcePlan.Files
            .Where(static file => string.Equals(
                file.RelativeOutputPath,
                SunderRpcAuthoringDocuments.AuthoringReceiptFileName,
                StringComparison.Ordinal))
            .Select(file =>
            {
                var item = new TaskItem(file.OutputPath);
                item.SetMetadata("Sha256", Convert.ToHexString(SHA256.HashData(file.Contents)).ToLowerInvariant());
                item.SetMetadata("LockSha256", sourcePlan.LockSha256);
                item.SetMetadata("PackageId", sourcePlan.PackageId);
                item.SetMetadata("PackageVersion", PackageVersion.Trim());
                item.SetMetadata("GeneratedDirectory", generatedDirectory);
                return (ITaskItem)item;
            })
            .ToArray();
        FileWrites = compileItems
            .Select(static item => item.ItemSpec)
            .Concat(snapshotPaths.Values)
            .Concat(sourceSnapshotPaths.Values)
            .Concat(sourcePlan.Files.Where(static file => !file.Compile).Select(static file => file.OutputPath))
            .Distinct(PathComparer)
            .Order(PathComparer)
            .Select(static path => (ITaskItem)new TaskItem(path))
            .ToArray();
    }

    private static ITaskItem GeneratedCompileItem(string path)
    {
        var item = new TaskItem(path);
        item.SetMetadata("AutoGen", "true");
        item.SetMetadata("DesignTime", "true");
        item.SetMetadata("Visible", "false");
        return item;
    }

    internal static ITaskItem SnapshotItem(
        string path,
        string sourceDescriptorPath,
        SunderRpcContractDescriptor descriptor,
        string generatedDirectory,
        string markerPath)
    {
        var item = new TaskItem(path);
        item.SetMetadata("SourceDescriptorPath", sourceDescriptorPath);
        item.SetMetadata("GeneratedDirectory", generatedDirectory);
        item.SetMetadata("OwnershipMarkerPath", markerPath);
        item.SetMetadata("DescriptorVersion", descriptor.DescriptorVersion.ToString(CultureInfo.InvariantCulture));
        item.SetMetadata("ContractId", descriptor.ContractId);
        item.SetMetadata("ContractVersion", descriptor.Version);
        item.SetMetadata("DescriptorSha256", descriptor.Sha256);
        item.SetMetadata("AbiSha256", descriptor.AbiSha256 ?? string.Empty);
        return item;
    }

    internal static string GetOwnershipMarkerPath(string generatedDirectory)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(generatedDirectory))
           + ValidateSunderDevOutputPathTask.MarkerFileName;

    internal static bool HasValidOwnershipMarker(string markerPath)
    {
        try
        {
            var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(
                markerPath,
                1024,
                "Sunder RPC generated-output marker");
            return bytes.AsSpan().SequenceEqual(Utf8NoBom.GetBytes(MarkerContents));
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException)
        {
            return false;
        }
    }

    internal static string SnapshotRelativePath(SunderRpcContractDescriptor descriptor)
        => Path.Combine(SnapshotDirectoryName, descriptor.Sha256 + ".rpc.json");

    private static void WriteStagedFile(string stagingRoot, string relativePath, ReadOnlySpan<byte> bytes)
    {
        var outputPath = Path.GetFullPath(Path.Combine(stagingRoot, relativePath));
        if (!GeneratedOutputPathSafety.IsDescendantOf(outputPath, stagingRoot))
        {
            throw new InvalidDataException($"Staged Sunder RPC output '{relativePath}' escapes its owned generated directory.");
        }
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);
        GeneratedOutputPathSafety.EnsureRegularDirectory(outputDirectory, "staged Sunder RPC output directory");
        WriteNewFile(outputPath, bytes);
        GeneratedOutputPathSafety.EnsureRegularFile(outputPath, "staged Sunder RPC output");
    }

    private static void WriteNewFile(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static bool TreesAreByteIdentical(string leftRoot, string rightRoot)
    {
        var left = ReadTree(leftRoot);
        var right = ReadTree(rightRoot);
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].RelativePath, right[index].RelativePath, StringComparison.Ordinal)
                || left[index].IsDirectory != right[index].IsDirectory
                || !left[index].Contents.AsSpan().SequenceEqual(right[index].Contents))
            {
                return false;
            }
        }
        return true;
    }

    private static IReadOnlyList<TreeEntry> ReadTree(string root)
    {
        GeneratedOutputPathSafety.EnsureRegularDirectory(root, "Sunder RPC generated directory");
        var entries = new List<TreeEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(current).Order(PathComparer))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new IOException($"Sunder RPC generated output '{path}' is not a regular file or directory.");
                }
                var relativePath = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    entries.Add(new TreeEntry(relativePath, true, []));
                    pending.Push(path);
                }
                else
                {
                    entries.Add(new TreeEntry(
                        relativePath,
                        false,
                        SunderRpcDescriptorFileReader.ReadStableBytes(
                            path,
                            int.MaxValue,
                            "Sunder RPC generated output")));
                }
            }
        }
        return entries.OrderBy(static entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static string DefaultOutputFile(SunderRpcContractDescriptor descriptor)
    {
        var identity = Utf8NoBom.GetBytes(descriptor.ContractId + "\0" + descriptor.Version);
        var identityHash = Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
        return $"SunderRpc.{identityHash}.g.cs";
    }

    private static bool TryNormalizeRelativeOutputFile(string value, out string normalized, out string error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "It must be a non-empty relative .cs path.";
            return false;
        }
        if (value[0] is '/' or '\\' || Path.IsPathRooted(value))
        {
            error = "It must be relative.";
            return false;
        }

        var portablePath = value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var segments = portablePath.Split(Path.DirectorySeparatorChar);
        const string portableInvalidCharacters = "<>:\"|?*";
        if (segments.Any(segment => segment.Length == 0
                                    || segment is "." or ".."
                                    || segment.EndsWith(' ')
                                    || segment.EndsWith('.')
                                    || segment.Any(character => char.IsControl(character)
                                                                || portableInvalidCharacters.Contains(character)
                                                                || Path.GetInvalidFileNameChars().Contains(character))))
        {
            error = "It contains an unsafe or non-portable path segment.";
            return false;
        }
        if (!string.Equals(Path.GetExtension(portablePath), ".cs", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(portablePath)))
        {
            error = "It must identify a .cs file.";
            return false;
        }

        normalized = portablePath;
        error = string.Empty;
        return true;
    }

    private static void DeleteStagedDirectory(string path)
    {
        GeneratedOutputPathSafety.EnsureRegularDirectory(path, "staged Sunder RPC generated directory");
        Directory.Delete(path, recursive: true);
    }

    private static void TryDeleteStagedDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (!GeneratedOutputPathSafety.PathExists(path)) return;
            if (Directory.Exists(path) && !GeneratedOutputPathSafety.IsReparsePoint(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A failed stage is outside the published ownership pair and can be collected later.
        }
    }

    private static void TryDeleteStagedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (!GeneratedOutputPathSafety.PathExists(path)) return;
            if (Directory.Exists(path) && !GeneratedOutputPathSafety.IsReparsePoint(path)) return;
            File.Delete(path);
        }
        catch
        {
            // A failed marker stage is outside the published ownership pair and can be collected later.
        }
    }

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record BindingPlan(
        string ItemSpec,
        string RelativeOutputPath,
        string OutputPath,
        byte[] SourceUtf8);

    private sealed record SourceGeneratedFile(
        string RelativeOutputPath,
        string OutputPath,
        byte[] Contents,
        bool Compile,
        SourceOutputScope Scope);

    private sealed record SourceAuthoringPlan(
        IReadOnlyList<SourceGeneratedFile> Files,
        IReadOnlyList<SunderRpcCSharpCompiledContract> Contracts,
        string PackageId,
        string LockSha256,
        IReadOnlyList<SunderRpcPackageProjectExportContract> ProjectImports,
        IReadOnlyList<SunderRpcCSharpImportedContract> Imports,
        SunderRpcAuthoringReceiptProjection? Projection)
    {
        public SunderRpcOwnedPackageBaseline? Baseline { get; init; }
        public SunderRpcInputSnapshot? Inputs { get; init; }

        public static SourceAuthoringPlan Empty { get; } = new([], [], string.Empty, string.Empty, [], [], null);

        public IEnumerable<ContractOutput> OutputContracts
            => Contracts.Select(static contract => new ContractOutput(contract.ContractKey, contract.Exposure, contract.Descriptor))
                .Concat(ProjectImports.Select(static contract => new ContractOutput(contract.ContractKey, contract.Exposure, contract.Descriptor)))
                .Concat(Imports.Select(static contract => new ContractOutput(contract.ContractKey, SunderServiceExposure.Public, contract.Descriptor)));
    }

    private sealed record ExpectedPackageProjectExport(
        string ExportPath,
        string GeneratedDirectory,
        string IntermediateOutputPath,
        string MarkerPath,
        string ExpectedSha256,
        string ProjectIdentity,
        string Configuration,
        string TargetFramework,
        string PackageId,
        string PackageVersion,
        string? LockSha256,
        IReadOnlyList<SunderRpcCSharpCompiledContract>? Contracts)
    {
        internal IReadOnlyList<SunderRpcRoleUse> RoleUses { get; init; } = [];
    }

    private sealed record ContractOutput(
        string ContractKey,
        SunderServiceExposure Exposure,
        SunderRpcContractDescriptor Descriptor);

    private sealed record TreeEntry(string RelativePath, bool IsDirectory, byte[] Contents);

    private enum RpcGenerationMode
    {
        Invalid,
        Combined,
        ProjectExport,
        Implementation,
        ProjectImport,
        EditorImports,
    }

    private enum SourceOutputScope
    {
        ProjectExport,
        Implementation,
    }
}
