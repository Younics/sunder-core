using Microsoft.Build.Framework;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Tests;

// Composes ordinary product operations for offline source-artifact evidence.
// The sibling Agent lane supplies fresh package candidates from normal Build/Pack.
public sealed class AgentSourceArtifactTestTask : Microsoft.Build.Utilities.Task
{
    [Required] public string ProjectDirectory { get; set; } = "";
    [Required] public string Operation { get; set; } = "";
    public string IntermediateOutputPath { get; set; } = "";
    public string AssemblyName { get; set; } = "Sunder.Package.Agent";
    public string ProjectIdentity { get; set; } = "AgentOwnerSource.csproj";
    public string PackageId { get; set; } = "sunder.package.agent";
    public string OutputType { get; set; } = "Exe";
    public string DefineConstants { get; set; } = "";
    public string CacheRoot { get; set; } = "";
    public string Candidate { get; set; } = "";
    public string ContractId { get; set; } = "";
    public string LocalName { get; set; } = "";
    public string ProjectionPath { get; set; } = "";
    public string RuntimeIdentifier { get; set; } = "";
    public ITaskItem[] SourceFiles { get; set; } = [];
    public ITaskItem[] ReferencePaths { get; set; } = [];
    [Output] public ITaskItem[] CompileItems { get; set; } = [];

    public override bool Execute()
    {
        try
        {
            var cache = string.IsNullOrEmpty(CacheRoot) ? Path.Combine(ProjectDirectory, "fixture-cache") : CacheRoot;
            if (Operation == "Project")
            {
                if (Directory.Exists(ProjectionPath) || File.Exists(ProjectionPath)) throw new InvalidOperationException("The test projection must be new.");
                var extracted = Path.Combine(ProjectDirectory, "obj", "projection-input-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var validated = SunderPackageArchiveInspector.ExtractAndValidateAsync(Candidate, extracted).GetAwaiter().GetResult();
                    if (!validated.Success) throw new InvalidDataException(string.Join(Environment.NewLine, validated.Errors));
                    var plan = SunderPackageTargetResolver.CreateProjectionPlan(validated.Manifest!, validated.ContentIndex!, new("runtime", RuntimeIdentifier));
                    Directory.CreateDirectory(ProjectionPath);
                    foreach (var file in plan.Files)
                    {
                        var target = file.LogicalPath.ToPlatformPath(ProjectionPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file.PhysicalPath.ToPlatformPath(extracted), target);
                    }
                    return true;
                }
                finally { if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true); }
            }
            if (Operation is "Add" or "Implement" or "Require")
            {
                using var http = new HttpClient(new NoNetwork());
                var validator = new ManageSunderPackageApiTask
                {
                    BuildEngine = BuildEngine, ProjectDirectory = ProjectDirectory, PackageVersion = "2.0.0",
                    SourceFiles = SourceFiles, ReferencePaths = ReferencePaths, AssemblyName = AssemblyName, ProjectIdentity = ProjectIdentity,
                    Configuration = "Debug", TargetFramework = "net10.0", OutputType = OutputType, DefineConstants = DefineConstants,
                    IntermediateOutputPath = IntermediateOutputPath,
                };
                var request = new SunderRpcPackageApiRequest(Operation, "sunder.package.agent",
                    ContractId: string.IsNullOrEmpty(ContractId) ? null : ContractId,
                    VersionRange: Operation == "Require" ? null : ">=2.0.0 <3.0.0",
                    RegistryOrigin: Operation == "Require" ? null : "https://registry.example/",
                    Presence: Operation == "Add" ? "required" : null,
                    LocalName: Operation == "Add" ? "agent" : null,
                    ProviderKey: Operation == "Implement" ? LocalName : null,
                    Candidate: Operation == "Require" ? null : Candidate);
                new SunderRpcPackageApiCommands(new(http)).ExecuteAsync(
                    Path.Combine(ProjectDirectory, "sunder-contracts.lock.json"), cache, request,
                    (before, _, stage) => validator.ValidateSource(before, stage, cache, request)).GetAwaiter().GetResult();
                return true;
            }
            var generation = new GenerateSunderRpcBindingsTask
            {
                BuildEngine = BuildEngine, ProjectDirectory = ProjectDirectory, PackageId = PackageId, PackageVersion = "2.0.0",
                AssemblyName = AssemblyName, ProjectIdentity = ProjectIdentity, Configuration = "Debug", TargetFramework = "net10.0",
                OutputType = OutputType, DefineConstants = DefineConstants, IntermediateOutputPath = IntermediateOutputPath,
                SourceFiles = SourceFiles, ReferencePaths = ReferencePaths, ContractCacheRoot = cache,
                ContractOperation = Operation == "Generate" ? "" : Operation,
            };
            var result = generation.Execute();
            CompileItems = generation.CompileItems;
            return result;
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
            => throw new InvalidOperationException("The Agent artifact lane must select and restore from its fresh local candidate offline.");
    }
}
