using Microsoft.Build.Framework;
using Sunder.Package.Build.Rpc.Authoring;

namespace Sunder.Package.Build.Tasks;

/// <summary>Imports verified publication evidence for an exact retained package into its immutable owned baseline.</summary>
public sealed class ImportSunderContractPublicationTask : Microsoft.Build.Utilities.Task, ICancelableTask
{
    private readonly CancellationTokenSource _cancellation = new();
    [Required] public string ProjectDirectory { get; set; } = string.Empty;
    [Required] public string PackagePath { get; set; } = string.Empty;
    [Required] public string RegistryOrigin { get; set; } = string.Empty;
    public string OwnedContractsPath { get; set; } = string.Empty;
    public bool AllowDevelopmentRegistryOrigin { get; set; }
    public void Cancel() => _cancellation.Cancel();
    public override bool Execute()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(PackagePath) || string.IsNullOrWhiteSpace(RegistryOrigin)) throw new InvalidDataException("Publication import requires an explicit retained package and Registry origin.");
            if (BuildEngine is IBuildEngine6 engine && new[] { "SunderContractKey", "SunderContractCurrentVersion", "SunderContractNextVersion",
                    "SunderAcknowledgeBreakingChange", "SunderPackageCandidatePath", "SunderPlatformCatalogPath" }.Any(engine.GetGlobalProperties().ContainsKey))
                throw new InvalidDataException("Publication import accepts exact retained-package evidence, not contract/version selection, acceptance or restore inputs.");
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            var changed = new SunderRpcPublicationImport(http, AllowDevelopmentRegistryOrigin).ImportAsync(ProjectDirectory, OwnedContractsPath, PackagePath, RegistryOrigin, _cancellation.Token).GetAwaiter().GetResult();
            Log.LogMessage(MessageImportance.High, changed ? "Imported exact Registry publication evidence into the owned baseline." : "Exact publication evidence is already retained; no files changed.");
            return true;
        }
        catch (Exception error) { Log.LogError("Publication import failed: {0}", error is InvalidDataException or IOException ? error.Message : error.GetType().Name); return false; }
    }
}
