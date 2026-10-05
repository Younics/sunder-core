using System.Globalization;
using Microsoft.Build.Framework;
using Sunder.Package.Build.Rpc.Authoring;

namespace Sunder.Package.Build.Tasks;

/// <summary>Explicitly collects aged contract-cache entries using reviewed source locks.</summary>
public sealed class CollectSunderContractCacheTask : Microsoft.Build.Utilities.Task, ICancelableTask
{
    private readonly CancellationTokenSource _cancellation = new();
    [Required] public string ProjectDirectory { get; set; } = string.Empty;
    [Required] public ITaskItem[] ReviewedLocks { get; set; } = [];
    public string ContractCacheRoot { get; set; } = string.Empty;
    public string GraceDays { get; set; } = string.Empty;
    [Output] public int RemovedEntries { get; private set; }
    [Output] public int RemovedArtifacts { get; private set; }
    [Output] public int RetainedUnprovenArtifacts { get; private set; }
    [Output] public bool MoreCandidates { get; private set; }

    public void Cancel() => _cancellation.Cancel();

    public override bool Execute()
    {
        try
        {
            if (BuildEngine is IBuildEngine6 commandEngine)
            {
                var properties = commandEngine.GetGlobalProperties();
                if (new[] { "SunderRegistryOrigin", "SunderAllowDevelopmentRegistryOrigin", "SunderPackageVersionRange", "SunderPackageIncludePrerelease", "SunderPackageApiPresence", "SunderPackageCandidatePath", "SunderPlatformCatalogPath" }.Any(properties.ContainsKey))
                    throw new InvalidDataException("contract-cache.entry-corrupt: GC does not accept Registry or selection inputs.");
                if (properties.ContainsKey("SunderContractCacheRoot") && string.IsNullOrWhiteSpace(ContractCacheRoot))
                    throw new InvalidDataException("contract-cache.entry-corrupt: An explicit cache root must be nonempty.");
            }
            var grace = GraceDays;
            if (grace.Length == 0 && !(BuildEngine is IBuildEngine6 engine && engine.GetGlobalProperties().ContainsKey("SunderContractCacheGraceDays"))) grace = "7";
            if (!int.TryParse(grace, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days.ToString(CultureInfo.InvariantCulture) != grace)
                throw new InvalidDataException("contract-cache.entry-corrupt: GC grace must be an integer from 1 through 365 days.");
            var root = string.IsNullOrEmpty(ContractCacheRoot) ? SunderRpcContractCache.DefaultRoot : Path.GetFullPath(ContractCacheRoot, ProjectDirectory);
            var result = SunderRpcContractCacheStore.Collect(root,
                ReviewedLocks.Select(item => Path.GetFullPath(item.ItemSpec, ProjectDirectory)).ToArray(), days, _cancellation.Token);
            RemovedEntries = result.RemovedEntries; RemovedArtifacts = result.RemovedArtifacts;
            RetainedUnprovenArtifacts = result.RetainedUnprovenArtifacts; MoreCandidates = result.MoreCandidates;
            Log.LogMessage(MessageImportance.High, "Collected {0} contract entries and {1} retained artifacts; kept {2} unproven artifacts. Pruned entries require explicit restore. More candidates: {3}.",
                RemovedEntries, RemovedArtifacts, RetainedUnprovenArtifacts, MoreCandidates);
            return true;
        }
        catch (Exception error)
        {
            Log.LogError("Contract cache GC failed: {0}", error is InvalidDataException ? error.Message : error.GetType().Name);
            return false;
        }
    }
}
