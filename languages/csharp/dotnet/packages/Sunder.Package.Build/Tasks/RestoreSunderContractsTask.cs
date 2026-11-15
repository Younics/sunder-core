using Microsoft.Build.Framework;
using Sunder.Package.Build.Rpc.Authoring;

namespace Sunder.Package.Build.Tasks;

/// <summary>Hydrates an existing exact contract lock without changing its selections or generated source.</summary>
public sealed class RestoreSunderContractsTask : Microsoft.Build.Utilities.Task, ICancelableTask
{
    private readonly CancellationTokenSource _cancellation = new();
    [Required] public string ProjectDirectory { get; set; } = string.Empty;
    public string ContractLockPath { get; set; } = "sunder-contracts.lock.json";
    public string ContractCacheRoot { get; set; } = SunderRpcContractCache.DefaultRoot;
    public ITaskItem[] CandidateArchives { get; set; } = [];
    public string PlatformCatalogPath { get; set; } = string.Empty;
    public bool AllowDevelopmentRegistryOrigin { get; set; }
    public bool VerifyOnly { get; set; }
    public bool OnlyMissing { get; set; }

    public void Cancel() => _cancellation.Cancel();

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
            Log.LogMessage(MessageImportance.High, "Sunder contract cache: {0}", cacheRoot);
            _cancellation.Token.ThrowIfCancellationRequested();
            var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(lockPath, AllowDevelopmentRegistryOrigin);
            if (VerifyOnly || OnlyMissing)
            {
                try
                {
                    _ = SunderRpcContractCacheSelection.Resolve(contractLock, cacheRoot, _cancellation.Token);
                    _cancellation.Token.ThrowIfCancellationRequested();
                    Log.LogMessage(MessageImportance.High, "Verified exact contract-cache readiness; no cache evidence or lock selections were changed.");
                    return true;
                }
                catch (InvalidDataException error) when (!VerifyOnly && OnlyMissing)
                {
                    if (!error.Message.StartsWith("contract-cache.entry-missing:", StringComparison.Ordinal)
                        && (!error.Message.StartsWith("contract-cache.entry-corrupt:", StringComparison.Ordinal) || !IsEmptyCacheRoot(cacheRoot))) throw;
                    // Restore a missing immutable selection, never repair corrupt
                    // evidence or reinterpret a changed lock as an API update. A
                    // truly empty directory has no evidence to adopt or repair.
                }
            }
            var candidates = OnlyMissing && contractLock.PackageArtifacts.Count == 0 ? []
                : CandidateArchives.Select(candidate => Path.GetFullPath(candidate.ItemSpec, ProjectDirectory)).ToArray();
            var catalogPath = OnlyMissing && SunderRpcPlatformCatalogCache.Requirements(contractLock).Count == 0 ? null
                : string.IsNullOrEmpty(PlatformCatalogPath) ? null : Path.GetFullPath(PlatformCatalogPath, ProjectDirectory);
            var count = new SunderRpcContractRestore(http, allowDevelopmentRegistryOrigin: AllowDevelopmentRegistryOrigin).RestoreAsync(lockPath, cacheRoot,
                candidates, _cancellation.Token, platformCatalog: catalogPath, onlyMissing: OnlyMissing).GetAwaiter().GetResult();
            _ = SunderRpcContractCacheSelection.Resolve(contractLock, cacheRoot, _cancellation.Token);
            Log.LogMessage(MessageImportance.High, "Restored {0} exact RPC descriptor cache entries; lock selections are unchanged.", count);
            return true;
        }
        catch (Exception error)
        {
            Log.LogError("Contract {0} failed: {1}", VerifyOnly ? "cache check" : "restore",
                error is InvalidDataException or IOException or UnauthorizedAccessException ? error.Message : error.GetType().Name);
            return false;
        }
    }

    private bool IsEmptyCacheRoot(string cacheRoot)
    {
        var root = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(cacheRoot);
        if (!GeneratedOutputPathSafety.PathExists(root)) return false;
        using var lease = GeneratedOutputLock.Acquire([root], TimeSpan.FromSeconds(30), _cancellation.Token);
        GeneratedOutputPathSafety.EnsureRegularDirectory(root, "uninitialized contract cache");
        return !Directory.EnumerateFileSystemEntries(root).Any();
    }
}
