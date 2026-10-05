using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Build.Framework;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Tests;

// Test-only catalog preparation used by the sibling Agent source lane. Its local
// Registry selection is fixture evidence; generation uses the ordinary product task.
public sealed class AgentStackSourceTestTask : Microsoft.Build.Utilities.Task
{
    [Required] public string CoreRoot { get; set; } = "";
    [Required] public string ProjectDirectory { get; set; } = "";
    public string Operation { get; set; } = "";
    public string IntermediateOutputPath { get; set; } = "obj/Debug/net10.0";
    public ITaskItem[] SourceFiles { get; set; } = [];
    public ITaskItem[] ReferencePaths { get; set; } = [];
    public string OutputType { get; set; } = "Library";
    public string DefineConstants { get; set; } = "";
    public bool GeneratePackageRoles { get; set; }
    public ITaskItem[] GeneratorAnalyzers { get; set; } = [];
    [Output] public ITaskItem[] CompileItems { get; set; } = [];
    [Output] public ITaskItem[] WorkerCompileItems { get; set; } = [];
    [Output] public ITaskItem[] BindingSnapshots { get; set; } = [];
    [Output] public ITaskItem[] ContractBundles { get; set; } = [];
    [Output] public ITaskItem[] RpcProviders { get; set; } = [];
    [Output] public ITaskItem[] RpcUses { get; set; } = [];
    [Output] public ITaskItem[] AuthoringReceipts { get; set; } = [];

    public override bool Execute()
    {
        try
        {
            var fixtureRoot = Path.Combine(CoreRoot, "specs/rpc/v1/authoring/conformance/valid");
            var cache = Path.Combine(ProjectDirectory, "fixture-cache");
            if (Operation == "PrepareCatalog")
            {
                var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(CoreRoot,
                    "languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/Contracts/sunder.stack.contributor.rpc.json")));
                var catalog = SunderPlatformContractCatalog.Verify(SunderPlatformContractCatalog.Create("1.1.0", [descriptor]));
                var locked = JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixtureRoot, "mixed-contract-lock.json")))!;
                locked["packageArtifacts"] = new JsonObject();
                locked["packageImports"] = new JsonObject();
                locked["providerImports"] = new JsonObject();
                var import = locked["platformImports"]!["stackContributor"]!;
                import["authorityReleaseRange"] = ">=1.1.0 <1.2.0";
                import["selectedAuthorityContentIdentity"] = catalog.ContentIdentity;
                import["descriptorBytes"] = descriptor.GetCanonicalUtf8().Length;
                import["descriptorSha256"] = descriptor.Sha256;
                import["abiSha256"] = descriptor.AbiSha256;
                import["etag"] = "\"" + descriptor.Sha256 + "\"";
                var lockPath = Path.Combine(ProjectDirectory, "sunder-contracts.lock.json");
                File.WriteAllBytes(lockPath, SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(locked)));
                var catalogPath = Path.Combine(ProjectDirectory, "fixture-catalog.json");
                File.WriteAllBytes(catalogPath, catalog.GetCanonicalUtf8());
                using var http = new HttpClient(new NoNetwork());
                new SunderRpcContractRestore(http).RestoreAsync(lockPath, cache, [], platformCatalog: catalogPath).GetAwaiter().GetResult();
                return true;
            }
            var generation = new GenerateSunderRpcBindingsTask
            {
                BuildEngine = BuildEngine, HostObject = HostObject, ProjectDirectory = ProjectDirectory,
                IntermediateOutputPath = IntermediateOutputPath, PackageId = "sunder.package.agent", PackageVersion = "2.0.0",
                ProjectIdentity = "AgentOwnerSource.csproj", TargetFramework = "net10.0", Configuration = "Debug",
                AssemblyName = "Sunder.Package.Agent", NullableEnabled = true, SourceFiles = SourceFiles, ReferencePaths = ReferencePaths,
                ContractCacheRoot = cache, ContractOperation = Operation,
                OutputType = OutputType, DefineConstants = DefineConstants,
                GeneratePackageRoles = GeneratePackageRoles, GeneratorAnalyzers = GeneratorAnalyzers,
            };
            var success = generation.Execute();
            CompileItems = generation.CompileItems;
            WorkerCompileItems = generation.WorkerCompileItems;
            BindingSnapshots = generation.BindingSnapshots;
            ContractBundles = generation.ContractBundles;
            RpcProviders = generation.RpcProviders;
            RpcUses = generation.RpcUses;
            AuthoringReceipts = generation.AuthoringReceipts;
            return success;
        }
        catch (Exception error)
        {
            Log.LogErrorFromException(error, showStackTrace: true);
            return false;
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The Agent source fixture must restore from its exact selected catalog offline.");
    }

}
