using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Tasks;

public sealed class PackSunderPackageTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string DevPackagePath { get; set; } = string.Empty;

    [Required]
    public string PackageOutputPath { get; set; } = string.Empty;

    [Required]
    public string DevPackageDiscoveryRoot { get; set; } = string.Empty;

    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    public ITaskItem[] SourceFiles { get; set; } = [];

    public ITaskItem[] ReferencePaths { get; set; } = [];

    public ITaskItem[] AuthoringReceipts { get; set; } = [];

    public ITaskItem[] ContractBundles { get; set; } = [];

    public ITaskItem[] UsesContracts { get; set; } = [];

    public ITaskItem[] RpcProviders { get; set; } = [];

    public string OwnedContractsPath { get; set; } = string.Empty;

    public string ContractLockPath { get; set; } = string.Empty;

    public string ContractCacheRoot { get; set; } = SunderRpcContractCache.DefaultRoot;
    public bool AllowDevelopmentRegistryOrigin { get; set; }


    public string ProjectIdentity { get; set; } = string.Empty;
    public string ProjectFile { get; set; } = string.Empty;
    public ITaskItem[] ProjectImports { get; set; } = [];

    public string AssemblyName { get; set; } = "Sunder.Rpc.Authoring";

    public string Configuration { get; set; } = string.Empty;

    public string TargetFramework { get; set; } = string.Empty;

    public string DefineConstants { get; set; } = string.Empty;

    public string OutputType { get; set; } = string.Empty;

    public bool NullableEnabled { get; set; } = true;

    public bool GeneratePackageRoles { get; set; }
    public ITaskItem[] ResourceItems { get; set; } = [];
    public ITaskItem[] GeneratorAnalyzers { get; set; } = [];
    public ITaskItem[] GeneratorProperties { get; set; } = [];
    public string RootNamespace { get; set; } = string.Empty;

    public string IntermediateOutputPath { get; set; } = "obj";

    internal Action? BeforeArchivePublication { get; set; }
    internal Action<SunderPackageManifest>? QualifiedPrebuiltValidation { get; set; }
    internal CancellationToken CancellationToken { get; set; }

    public override bool Execute()
    {
        string? stagedPackagePath = null;
        try
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (!GeneratedOutputPathSafety.TryValidateArchive(
                    PackageOutputPath,
                    ProjectDirectory,
                    out var packageOutputPath,
                    out var pathError))
            {
                Log.LogError($"Sunder package output path '{PackageOutputPath}' is unsafe. {pathError}");
                return false;
            }

            var devPackagePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DevPackagePath));
            var discoveryKey = GeneratedOutputLock.TargetLeafDiscoveryKey(DevPackageDiscoveryRoot, devPackagePath);
            using var outputLock = GeneratedOutputLock.Acquire([discoveryKey], [devPackagePath, packageOutputPath], cancellationToken: CancellationToken);
            GeneratedOutputTransaction.RecoverAndCleanup(packageOutputPath);
            GeneratedOutputTransaction.RecoverAndCleanup(
                devPackagePath,
                ValidateSunderDevOutputPathTask.GetOwnershipMarkerPath(devPackagePath),
                devPackagePath + AggregateSunderPackageTask.MarkerFileSuffix);
            if (Directory.Exists(packageOutputPath)
                || File.Exists(packageOutputPath)
                && GeneratedOutputPathSafety.IsReparsePoint(packageOutputPath))
            {
                Log.LogError($"Sunder package output path '{PackageOutputPath}' is no longer a safe regular file after lock acquisition.");
                return false;
            }

            if (!Directory.Exists(devPackagePath))
            {
                Log.LogError($"Sunder dev package folder '{DevPackagePath}' does not exist.");
                return false;
            }

            if ((File.GetAttributes(devPackagePath) & FileAttributes.ReparsePoint) != 0)
            {
                Log.LogError($"Sunder dev package folder '{DevPackagePath}' must not be a symbolic link or reparse point.");
                return false;
            }

            var manifestPath = ArchiveRelativePath.Parse(SunderPackageFormat.ManifestPath).ToPlatformPath(devPackagePath);
            if (!File.Exists(manifestPath))
            {
                Log.LogError(
                    $"Sunder dev package folder '{DevPackagePath}' does not contain {SunderPackageFormat.ManifestPath}.");
                return false;
            }

            if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
            {
                Log.LogError($"Sunder dev package manifest '{manifestPath}' must not be a symbolic link or reparse point.");
                return false;
            }

            var archiveValidationPath = Path.Combine(
                Path.GetTempPath(),
                "Sunder.Package.Build",
                "pack-validation",
                Guid.NewGuid().ToString("N"));
            try
            {
                var stagingValidation = SunderPackageArchiveInspector
                    .ValidateExtractedPackageAsync(devPackagePath, CancellationToken)
                    .GetAwaiter()
                    .GetResult();
                if (!LogValidation(stagingValidation, logWarnings: false))
                {
                    return false;
                }

                QualifiedPrebuiltValidation?.Invoke(stagingValidation.Manifest!);
                var contractPlan = QualifiedPrebuiltValidation is null ? ValidateSourceContracts(stagingValidation.Manifest!)
                    : new ContractPackPlan(stagingValidation.Manifest!, new SunderRpcInputSnapshot(), null, null);
                if (Log.HasLoggedErrors) return false;
                ValidateModelProjection(contractPlan, devPackagePath);

                var packageOutputDirectory = Path.GetDirectoryName(packageOutputPath);
                if (!string.IsNullOrWhiteSpace(packageOutputDirectory))
                {
                    Directory.CreateDirectory(packageOutputDirectory);
                }

                stagedPackagePath = packageOutputPath + ".stage-" + Guid.NewGuid().ToString("N");
                SunderArchive.WriteDeterministicAsync(devPackagePath, stagedPackagePath, CancellationToken).GetAwaiter().GetResult();
                var archiveValidation = SunderPackageArchiveInspector
                    .ExtractAndValidateAsync(stagedPackagePath, archiveValidationPath, CancellationToken)
                    .GetAwaiter()
                    .GetResult();
                if (!LogValidation(archiveValidation))
                {
                    return false;
                }
                using var baselineLock = contractPlan.BaselinePath is null ? null : GeneratedOutputLock.Acquire([contractPlan.BaselinePath]);
                contractPlan.Inputs.Verify();
                if (contractPlan.BaselinePath is not null)
                    GenerateSunderRpcBindingsTask.VerifyExpectedBaseline(ProjectDirectory, contractPlan.BaselinePath, contractPlan.BaselineBytes);
                ValidateProjection(contractPlan, archiveValidation.Manifest!);
                ValidateModelProjection(contractPlan, archiveValidationPath);
                QualifiedPrebuiltValidation?.Invoke(archiveValidation.Manifest!);
                BeforeArchivePublication?.Invoke();
                contractPlan.Inputs.Verify();
                QualifiedPrebuiltValidation?.Invoke(archiveValidation.Manifest!);
                CancellationToken.ThrowIfCancellationRequested();
                if (contractPlan.BaselinePath is not null)
                    GenerateSunderRpcBindingsTask.VerifyExpectedBaseline(ProjectDirectory, contractPlan.BaselinePath, contractPlan.BaselineBytes);
                GeneratedOutputTransaction.Commit(
                    new GeneratedOutputTransaction.StagedOutput(packageOutputPath, stagedPackagePath));
                stagedPackagePath = null;
                Log.LogMessage(MessageImportance.High, $"Packed Sunder package to {packageOutputPath}");
                return true;
            }
            finally
            {
                TryDeleteDirectory(archiveValidationPath);
            }
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: false);
            return false;
        }
        finally
        {
            if (stagedPackagePath is not null) TryDeleteFile(stagedPackagePath);
        }
    }

    private ContractPackPlan ValidateSourceContracts(SunderPackageManifest manifest)
    {
        var projectDirectory = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(ProjectDirectory);
        var baselinePath = Path.GetFullPath(string.IsNullOrWhiteSpace(OwnedContractsPath)
            ? SunderRpcAuthoringDocuments.OwnedContractsFileName : OwnedContractsPath, projectDirectory);
        var lockPath = Path.GetFullPath(string.IsNullOrWhiteSpace(ContractLockPath)
            ? SunderRpcAuthoringDocuments.ContractLockFileName : ContractLockPath, projectDirectory);
        var generatedDirectory = Path.Combine(Path.GetFullPath(IntermediateOutputPath, projectDirectory), GenerateSunderRpcBindingsTask.GeneratedDirectoryName);
        var receiptPath = Path.Combine(generatedDirectory, SunderRpcAuthoringDocuments.AuthoringReceiptFileName);
        var exportPath = Path.Combine(generatedDirectory, SunderRpcAuthoringDocuments.PackageExportFileName);
        var inputs = new SunderRpcInputSnapshot();
        var generation = new GenerateSunderRpcBindingsTask
        {
            BuildEngine = BuildEngine, HostObject = HostObject,
            ProjectDirectory = projectDirectory, IntermediateOutputPath = IntermediateOutputPath,
            SourceFiles = SourceFiles, ReferencePaths = ReferencePaths, PackageId = manifest.Id!, PackageVersion = manifest.Version!,
            ProjectIdentity = ProjectIdentity, Configuration = Configuration, TargetFramework = TargetFramework, AssemblyName = AssemblyName,
            ProjectFile = ProjectFile, ProjectImports = ProjectImports,
            DefineConstants = DefineConstants, OutputType = OutputType, NullableEnabled = NullableEnabled,
            GeneratePackageRoles = GeneratePackageRoles,
            ResourceItems = ResourceItems, GeneratorAnalyzers = GeneratorAnalyzers, GeneratorProperties = GeneratorProperties, RootNamespace = RootNamespace,
            OwnedContractsPath = baselinePath, ContractLockPath = lockPath, ContractCacheRoot = ContractCacheRoot,
            AllowDevelopmentRegistryOrigin = AllowDevelopmentRegistryOrigin,
        };
        var sources = generation.ReadSourceFiles(projectDirectory, inputs);
        if (sources is null)
            throw new InvalidDataException("Pack could not read the ordinary C# contract source inputs.");
        if (AuthoringReceipts.Length == 0 && string.IsNullOrWhiteSpace(OwnedContractsPath) && string.IsNullOrWhiteSpace(ContractLockPath)
            && !GeneratedOutputPathSafety.PathExists(baselinePath)
            && !GeneratedOutputPathSafety.PathExists(lockPath)
            && !GeneratedOutputPathSafety.PathExists(receiptPath) && !GeneratedOutputPathSafety.PathExists(exportPath)
            && !GenerateSunderRpcBindingsTask.HasSourceAuthoredServiceAttribute(sources, DefineConstants))
        {
            inputs.ExpectAbsent(baselinePath);
            inputs.ExpectAbsent(lockPath);
            inputs.ExpectAbsent(receiptPath);
            inputs.ExpectAbsent(exportPath);
            if (ContractBundles.Length == 0)
            {
                if (HasRpc(manifest) || UsesContracts.Length != 0 || RpcProviders.Length != 0)
                    throw new InvalidDataException("RPC-bearing artifacts require current source/frozen baseline provenance or explicit authored SunderContractBundle descriptors. Artifact-only RPC packing is disabled.");
                return new(new SunderPackageManifest { Id = manifest.Id, Version = manifest.Version }, inputs, null, null);
            }

            // Descriptor-first migration is explicit, locally owned input, not inferred release
            // evidence. Reuse the manifest builder without publishing descriptor output files.
            foreach (var item in ContractBundles)
            {
                var descriptorPath = Path.GetFullPath(item.ItemSpec, projectDirectory);
                if (GeneratedOutputPathSafety.IsDescendantOf(descriptorPath, DevPackagePath)
                    || GeneratedOutputPathSafety.IsDescendantOf(descriptorPath, generatedDirectory)
                    || !string.IsNullOrEmpty(item.GetMetadata("GeneratedDirectory")))
                    throw new InvalidDataException("Descriptor-first packing requires authored source descriptors, not extracted or generated RPC snapshots.");
                inputs.Read(descriptorPath, checked((int)SunderPackageFormat.MaxContractDescriptorBytes), "authored SunderContractBundle descriptor");
            }
            var descriptorPlan = PackageContractBuilder.Build(
                Path.Combine(projectDirectory, IntermediateOutputPath, "sunder-package.json"), projectDirectory, manifest.Id!, manifest.Version!,
                manifest.DependsOn ?? [], manifest.Targets ?? [], ContractBundles, [], UsesContracts, RpcProviders, [], BuildEngine, null, Log,
                publishFiles: false) ?? throw new InvalidDataException("Explicit descriptor-first RPC inputs are invalid.");
            var migration = new ContractPackPlan(descriptorPlan.Manifest, inputs, baselinePath, null);
            inputs.Verify();
            ValidateProjection(migration, manifest);
            return migration;
        }

        baselinePath = GenerateSunderRpcBindingsTask.ResolveOwnedBaselinePath(projectDirectory, baselinePath);
        if (!GeneratedOutputPathSafety.PathExists(baselinePath))
        {
            if (manifest.ContractBundles?.Any(bundle => bundle is { Source: "owned", Exposure: "public" }) == true)
                throw new InvalidDataException("Public source contracts cannot be packed by deleting the baseline. Restore the existing owned-contract baseline before packing.");
            GenerateSunderRpcBindingsTask.VerifyNoLostPublicBaseline(projectDirectory, generatedDirectory);
        }
        if (SourceFiles.Length == 0)
            throw new InvalidDataException("Source-authored RPC packaging requires ordinary SourceFiles and resolved ReferencePaths, including when packing prebuilt leaves.");
        var baseline = GeneratedOutputPathSafety.PathExists(baselinePath) ? SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(baselinePath) : null;
        if (baseline is null) inputs.ExpectAbsent(baselinePath); else inputs.Record(baselinePath, baseline.ExactBytes);
        if (baseline is not null && baseline.PackageId != manifest.Id)
            throw new InvalidDataException("The source owned-contract baseline belongs to a different package than the pack input.");
        foreach (var (key, contract) in baseline?.Contracts ?? new Dictionary<string, SunderRpcOwnedContractBaseline>())
        {
            if (SunderRpcAuthoringDocuments.HasPublicEvidence(contract) && contract.Current is { State: not ("release-frozen" or "published") })
                throw new InvalidDataException($"Owned contract '{key}' is draft. Run FreezeSunderContracts explicitly before PackSunderPackage.");
        }
        var candidate = generation.PrepareOwnedContractCandidate(baseline);
        if (candidate is null)
            throw new InvalidDataException("Pack requires unchanged source and owned contracts, with a complete offline lock/cache and valid generated bindings.");
        inputs.Merge(candidate.Inputs);
        var automaticAppUses = GeneratePackageRoles ? null : candidate.Contracts.Select(contract => (Contract: contract,
            Policy: SunderRpcAuthoringDocuments.ResolveAppUse(contract.Exposure, contract.Descriptor, contract.AppUse)))
            .Where(item => item.Policy is not null).Select(item => new SunderPackageContractUseManifest
        {
            Role = "app", ContractId = item.Contract.Descriptor.ContractId, ContractVersion = item.Contract.Descriptor.Version,
            AbiSha256 = item.Contract.Descriptor.AbiSha256, Required = item.Policy!.Required,
            Actions = item.Policy.Actions.ToArray(),
        }).ToArray();
        var plan = new ContractPackPlan(candidate.Projection.Manifest, inputs, baselinePath, automaticAppUses, baseline?.ExactBytes,
            SunderRpcAuthoringDocuments.PublicModelProjectionBytes(candidate.Projection));
        ValidateProjection(plan, manifest);
        foreach (var receipt in AuthoringReceipts)
        {
            var bytes = inputs.Read(Path.GetFullPath(receipt.ItemSpec, projectDirectory),
                67_108_864, "pack authoring receipt");
            var expectedHash = receipt.GetMetadata("Sha256");
            if (expectedHash.Length != 0 && expectedHash != Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())
                throw new InvalidDataException("The authoring receipt changed after generation.");
            var projection = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(bytes,
                candidate.LockSha256, manifest.Id!, manifest.Version!);
            ValidateProjection(plan, projection.Manifest);
            foreach (var (key, modelProjection) in projection.ModelProjections)
                if (!candidate.Projection.ModelProjections.TryGetValue(key, out var sourceProjection)
                    || !SunderRpcAuthoringDocuments.SameModelProjection(sourceProjection, modelProjection)
                    || !SunderRpcAuthoringDocuments.SameMethodPolicies(candidate.Projection.MethodPolicies[key], projection.MethodPolicies[key]))
                    throw new InvalidDataException("The pack receipt model projection differs from the current compiled source/import selection.");
        }
        inputs.Verify();
        return plan;
    }

    private static bool HasRpc(SunderPackageManifest manifest)
        => manifest.ContractBundles is { Count: > 0 } || manifest.Provides is { Count: > 0 } || manifest.UsesContracts is { Count: > 0 }
            || manifest.PackageArtifactImports is { Count: > 0 } || manifest.PackageApiImports is { Count: > 0 }
            || manifest.ProviderImports is { Count: > 0 } || manifest.PlatformImports is { Count: > 0 };

    private static void ValidateProjection(ContractPackPlan plan, SunderPackageManifest actual)
        => RequireMatchingProjection(plan.Manifest, actual, plan.AutomaticAppUses);

    private static void ValidateModelProjection(ContractPackPlan plan, string root)
    {
        if (plan.ModelProjectionUtf8 is null) return;
        var path = ArchiveRelativePath.Parse(SunderRpcAuthoringDocuments.ModelProjectionArchivePath).ToPlatformPath(root);
        var actual = SunderRpcDescriptorFileReader.ReadStableBytes(path, 4_194_304, "pack model projection");
        if (!actual.AsSpan().SequenceEqual(plan.ModelProjectionUtf8))
            throw new InvalidDataException("The pack input public model projection does not match current source/import evidence. Rebuild the stale leaves.");
    }

    internal static void RequireMatchingProjection(SunderPackageManifest expected, SunderPackageManifest actual,
        IReadOnlyList<SunderPackageContractUseManifest?>? automaticAppUses = null)
    {
        if (expected.Id != actual.Id || expected.Version != actual.Version)
            throw new InvalidDataException("The pack input package identity changed after contract validation.");
        Match(expected.ContractBundles, actual.ContractBundles, "contract bundles");
        Match(expected.Provides, actual.Provides, "providers");
        Match((expected.UsesContracts ?? []).Where(static use => use!.Role != "app"),
            (actual.UsesContracts ?? []).Where(static use => use!.Role != "app"), "runtime contract uses");
        var appUses = (actual.UsesContracts ?? []).Where(static use => use!.Role == "app").ToArray();
        // An App projects the source policy, including optional/narrowed uses. A
        // removed or broadened grant cannot be hidden by editing prebuilt output.
        var expectedAppUses = automaticAppUses is not null
            && (actual.Targets?.Any(target => target!.Role == "app") == true || appUses.Length != 0)
            ? automaticAppUses : (expected.UsesContracts ?? []).Where(static use => use!.Role == "app");
        Match(expectedAppUses, appUses, "App contract uses");
        Match(expected.PackageArtifactImports, actual.PackageArtifactImports, "package artifact imports");
        Match(expected.PackageApiImports, actual.PackageApiImports, "package API imports");
        Match(expected.ProviderImports, actual.ProviderImports, "provider imports");
        Match(expected.PlatformImports, actual.PlatformImports, "platform imports");

        static void Match<T>(IEnumerable<T>? left, IEnumerable<T>? right, string label)
        {
            var expectedItems = (left ?? []).Select(static item => JsonSerializer.Serialize(item)).Order(StringComparer.Ordinal);
            var actualItems = (right ?? []).Select(static item => JsonSerializer.Serialize(item)).Order(StringComparer.Ordinal);
            if (!expectedItems.SequenceEqual(actualItems, StringComparer.Ordinal))
                throw new InvalidDataException($"The pack input {label} do not match the current source-authoring projection. Rebuild the prebuilt leaves before packing.");
        }
    }

    private sealed record ContractPackPlan(
        SunderPackageManifest Manifest,
        SunderRpcInputSnapshot Inputs,
        string? BaselinePath,
        IReadOnlyList<SunderPackageContractUseManifest?>? AutomaticAppUses,
        byte[]? BaselineBytes = null,
        byte[]? ModelProjectionUtf8 = null);

    private bool LogValidation(SunderPackageArchiveValidationResult validation, bool logWarnings = true)
    {
        if (logWarnings)
        {
            foreach (var warning in validation.Warnings)
            {
                Log.LogWarning(warning);
            }
        }
        foreach (var error in validation.Errors)
        {
            Log.LogError(error);
        }
        return validation.Success;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup for temporary package staging.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup for a package archive that failed final validation.
        }
    }
}
