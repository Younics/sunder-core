using Microsoft.Build.Framework;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Tasks;

/// <summary>Performs an explicit, transactional package API lock operation.</summary>
public sealed class ManageSunderPackageApiTask : Microsoft.Build.Utilities.Task, ICancelableTask
{
    private readonly CancellationTokenSource _cancellation = new();

    [Required] public string Operation { get; set; } = string.Empty;
    [Required] public string ProjectDirectory { get; set; } = string.Empty;
    public string ContractLockPath { get; set; } = string.Empty;
    public string ContractCacheRoot { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string ContractId { get; set; } = string.Empty;
    public string VersionRange { get; set; } = string.Empty;
    public string IncludePrerelease { get; set; } = string.Empty;
    public string RegistryOrigin { get; set; } = string.Empty;
    public bool AllowDevelopmentRegistryOrigin { get; set; }
    public string Presence { get; set; } = string.Empty;
    public string ConsumerRoles { get; set; } = string.Empty;
    public string LocalName { get; set; } = string.Empty;
    public string Candidate { get; set; } = string.Empty;
    public string PlatformCatalogPath { get; set; } = string.Empty;
    public ITaskItem[] SourceFiles { get; set; } = [];
    public ITaskItem[] ReferencePaths { get; set; } = [];
    public string PackageVersion { get; set; } = string.Empty;
    public string OwnedContractsPath { get; set; } = string.Empty;
    public string IntermediateOutputPath { get; set; } = string.Empty;
    public string ProjectIdentity { get; set; } = string.Empty;
    public string ProjectFile { get; set; } = string.Empty;
    public ITaskItem[] ProjectImports { get; set; } = [];
    public string AssemblyName { get; set; } = "Sunder.Rpc.Authoring";
    public string Configuration { get; set; } = string.Empty;
    public string TargetFramework { get; set; } = string.Empty;
    public string OutputType { get; set; } = string.Empty;
    public string DefineConstants { get; set; } = string.Empty;
    public bool NullableEnabled { get; set; } = true;
    public bool GeneratePackageRoles { get; set; }
    public ITaskItem[] ResourceItems { get; set; } = [];
    public ITaskItem[] GeneratorAnalyzers { get; set; } = [];
    public ITaskItem[] GeneratorProperties { get; set; } = [];
    public string RootNamespace { get; set; } = string.Empty;

    public void Cancel() => _cancellation.Cancel();

    public override bool Execute()
    {
        try
        {
            var prerelease = Option("SunderPackageIncludePrerelease", IncludePrerelease);
            if (prerelease is not null and not ("true" or "false"))
                throw new InvalidDataException("package-api.import-not-found: SunderPackageIncludePrerelease must be absent or exactly true/false.");
            var candidate = Option("SunderPackageCandidatePath", Candidate);
            var platformCatalog = Option("SunderPlatformCatalogPath", PlatformCatalogPath);
            var localName = Option("SunderPackageApiLocalName", LocalName);
            var request = new SunderRpcPackageApiRequest(Operation,
                Option("SunderPackageId", PackageId), Option("SunderContractId", ContractId), Option("SunderPackageVersionRange", VersionRange),
                prerelease is null ? null : prerelease == "true", Option("SunderRegistryOrigin", RegistryOrigin),
                Option("SunderPackageApiPresence", Presence), Operation == "Implement" ? null : localName,
                Operation == "Implement" ? localName : null, candidate is null or "" ? candidate : Path.GetFullPath(candidate, ProjectDirectory),
                 platformCatalog is null or "" ? platformCatalog : Path.GetFullPath(platformCatalog, ProjectDirectory), Option("SunderPackageApiConsumerRoles", ConsumerRoles));
            var lockPath = Path.GetFullPath(string.IsNullOrEmpty(ContractLockPath) ? SunderRpcAuthoringDocuments.ContractLockFileName : ContractLockPath, ProjectDirectory);
            var cacheRoot = string.IsNullOrEmpty(ContractCacheRoot) ? SunderRpcContractCache.DefaultRoot : Path.GetFullPath(ContractCacheRoot, ProjectDirectory);
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            var result = new SunderRpcPackageApiCommands(new(http, AllowDevelopmentRegistryOrigin)).ExecuteAsync(lockPath, cacheRoot, request,
                (before, _, stage) => ValidateSource(before, stage, cacheRoot, request), _cancellation.Token,
                message => Log.LogMessage(MessageImportance.High, message)).GetAwaiter().GetResult();
            Log.LogMessage(MessageImportance.High, result.Changed ? "Committed package API lock changes. Run Build to regenerate bindings." : "Package API lock is unchanged.");
            foreach (var artifact in result.ContractLock.PackageArtifacts.Values)
            {
                var consumer = result.ContractLock.PackageImports.GetValueOrDefault(artifact.PackageId);
                Log.LogMessage(MessageImportance.High, "{0} {1} ({2}; prerelease={3}; presence={4}; {5} public contracts)", artifact.PackageId,
                    artifact.SelectedPackageVersion, artifact.RequestedVersionRange, artifact.IncludePrerelease, consumer?.Presence ?? "provider-only", artifact.PublicContractCount);
                foreach (var contract in artifact.Contracts.Values)
                    Log.LogMessage(MessageImportance.Normal, "  {0} {1}; descriptor={2}; ABI={3}; authority={4}/{5}; required={6}",
                        contract.ContractId, contract.ResolvedContractVersion, contract.DescriptorSha256, contract.AbiSha256,
                        contract.AuthorityKind, contract.AuthorityId, consumer?.RequiredContracts.Contains(contract.ContractId) == true);
            }
            foreach (var import in result.ContractLock.PlatformImports.Values)
                Log.LogMessage(MessageImportance.High, "platform {0}: {1} {2}; catalog={3}; registry={4}",
                    import.Key, import.ContractId, import.ResolvedContractVersion, import.SelectedAuthorityContentIdentity, import.RegistryOrigin ?? "local");
            if (result.CandidateAuthorityUnconfirmed)
                Log.LogMessage(MessageImportance.High, "package-api.candidate-authority-unconfirmed: Candidate bytes support local builds; Registry publication must confirm ownership before dependent publication.");
            return true;
        }
        catch (Exception error)
        {
            Log.LogError("Package API operation failed: {0}", error is InvalidDataException ? error.Message : error.GetType().Name);
            return false;
        }
    }

    private string? Option(string property, string value)
        => value.Length > 0 ? value : BuildEngine is IBuildEngine6 engine && engine.GetGlobalProperties().TryGetValue(property, out var supplied) ? supplied : null;

    internal void ValidateSource(SunderRpcContractLock before, string stage, string cacheRoot, SunderRpcPackageApiRequest request)
    {
        string? providerKey = null;
        if (request.Operation is "Implement" or "Unimplement" or "PlatformImplement")
        {
            var selected = request.Operation == "Unimplement" ? before : SunderRpcAuthoringDocuments.ReadContractLock(stage, AllowDevelopmentRegistryOrigin);
            providerKey = request.Operation == "PlatformImplement" ? selected.PlatformImports.Values.Single(value => value.ContractId == request.ContractId).Key
                : selected.ProviderImports.Values.Single(value => value.PackageId == request.PackageId && value.ContractId == request.ContractId).Key;
        }
        if (request.Operation is not ("Add" or "Update" or "Implement" or "Unimplement" or "PlatformImplement" or "PlatformUpdate" or "PlatformUnimplement" or "MigrateConsumerRoles")) return;
        var check = new GenerateSunderRpcBindingsTask
        {
            BuildEngine = BuildEngine, SourceFiles = SourceFiles, ReferencePaths = ReferencePaths,
            ProjectDirectory = ProjectDirectory, PackageVersion = PackageVersion,
            ContractLockPath = request.Operation == "Unimplement"
                ? Path.GetFullPath(string.IsNullOrEmpty(ContractLockPath) ? SunderRpcAuthoringDocuments.ContractLockFileName : ContractLockPath, ProjectDirectory) : stage,
            ContractCacheRoot = cacheRoot,
            OwnedContractsPath = OwnedContractsPath, IntermediateOutputPath = IntermediateOutputPath, ProjectIdentity = ProjectIdentity,
            ProjectFile = ProjectFile, ProjectImports = ProjectImports,
            Configuration = Configuration, TargetFramework = TargetFramework, OutputType = OutputType, DefineConstants = DefineConstants, AssemblyName = AssemblyName,
            NullableEnabled = NullableEnabled, GeneratePackageRoles = GeneratePackageRoles, ResourceItems = ResourceItems,
            GeneratorAnalyzers = GeneratorAnalyzers, GeneratorProperties = GeneratorProperties, RootNamespace = RootNamespace,
            AllowDevelopmentRegistryOrigin = AllowDevelopmentRegistryOrigin,
        };
        check.ValidateApiSelection(request.Operation, providerKey);
    }
}
