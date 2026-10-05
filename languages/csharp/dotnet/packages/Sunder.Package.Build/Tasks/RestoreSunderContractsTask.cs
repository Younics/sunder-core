using Microsoft.Build.Framework;
using Sunder.Package.Build.Rpc.Authoring;

namespace Sunder.Package.Build.Tasks;

/// <summary>Hydrates an existing exact contract lock without changing its selections or generated source.</summary>
public sealed class RestoreSunderContractsTask : Microsoft.Build.Utilities.Task
{
    [Required] public string ProjectDirectory { get; set; } = string.Empty;
    public string ContractLockPath { get; set; } = "sunder-contracts.lock.json";
    public string ContractCacheRoot { get; set; } = SunderRpcContractCache.DefaultRoot;
    public ITaskItem[] CandidateArchives { get; set; } = [];
    public string PlatformCatalogPath { get; set; } = string.Empty;
    public bool AllowDevelopmentRegistryOrigin { get; set; }

    public override bool Execute()
    {
        try
        {
            if (BuildEngine is IBuildEngine6 engine)
            {
                var properties = engine.GetGlobalProperties();
                if (properties.ContainsKey("SunderRegistryOrigin"))
                    throw new InvalidDataException("package-api.registry-origin-invalid: Restore rejects a Registry origin override, including an empty value.");
                if (properties.TryGetValue("SunderPackageCandidatePath", out var candidate) && string.IsNullOrWhiteSpace(candidate))
                    throw new InvalidDataException("package-api.public-inventory-invalid: An explicit candidate path must be nonempty; offline restore cannot fall back to network.");
                if (properties.TryGetValue("SunderPlatformCatalogPath", out var catalog) && string.IsNullOrWhiteSpace(catalog))
                    throw new InvalidDataException("platform-api.catalog-invalid: An explicit platform catalog input must be nonempty; offline restore cannot fall back to network.");
            }
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            var lockPath = Path.GetFullPath(string.IsNullOrWhiteSpace(ContractLockPath) ? "sunder-contracts.lock.json" : ContractLockPath, ProjectDirectory);
            var cacheRoot = string.IsNullOrWhiteSpace(ContractCacheRoot) ? SunderRpcContractCache.DefaultRoot : Path.GetFullPath(ContractCacheRoot, ProjectDirectory);
            var count = new SunderRpcContractRestore(http, allowDevelopmentRegistryOrigin: AllowDevelopmentRegistryOrigin).RestoreAsync(lockPath, cacheRoot,
                CandidateArchives.Select(candidate => Path.GetFullPath(candidate.ItemSpec, ProjectDirectory)).ToArray(),
                platformCatalog: string.IsNullOrEmpty(PlatformCatalogPath) ? null : Path.GetFullPath(PlatformCatalogPath, ProjectDirectory)).GetAwaiter().GetResult();
            Log.LogMessage(MessageImportance.High, "Restored {0} exact RPC descriptor cache entries; lock selections are unchanged.", count);
            return true;
        }
        catch (Exception error)
        {
            Log.LogError("Contract restore failed: {0}", error is InvalidDataException ? error.Message : error.GetType().Name);
            return false;
        }
    }
}
