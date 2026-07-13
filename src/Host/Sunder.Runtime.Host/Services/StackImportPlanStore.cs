using System.Collections.Concurrent;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Services;

internal sealed class StackImportPlanStore(
    RuntimeContentTransferStore transfers,
    TimeProvider timeProvider,
    RuntimeStackPolicyOptions policy) : IDisposable
{
    private readonly ConcurrentDictionary<string, StackImportPlan> _plans = new(StringComparer.Ordinal);

    public DateTimeOffset ExpiresAtUtc => timeProvider.GetUtcNow() + policy.ImportPlanLifetime;

    public bool TryAdd(StackImportPlan plan) => _plans.TryAdd(plan.PlanId, plan);

    public bool TryTake(string planId, out StackImportPlan plan) => _plans.TryRemove(planId, out plan!);

    public bool Discard(string planId)
    {
        if (!TryTake(planId, out var plan)) return false;
        Release(plan);
        return true;
    }

    public void SweepExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var pair in _plans)
        {
            if (pair.Value.ExpiresAtUtc <= now && _plans.TryRemove(pair)) Release(pair.Value);
        }
    }

    public bool IsExpired(StackImportPlan plan) => plan.ExpiresAtUtc <= timeProvider.GetUtcNow();

    public void Release(StackImportPlan plan)
    {
        TryDeleteDirectory(plan.StagingPath);
        transfers.ReleaseUpload(plan.Upload);
    }

    public void Dispose()
    {
        foreach (var planId in _plans.Keys) Discard(planId);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}

internal sealed record StackContributorRegistration(string PackageId, IPackageStackContributor Contributor);

internal sealed record StackContributorBinding(
    StackContributorRegistration Registration,
    IReadOnlyList<StackFragmentImport> Fragments,
    IReadOnlyList<RuntimeStackImportActionDescriptor> Actions);

internal sealed record StackImportPlan(
    string PlanId,
    RuntimeUploadLease Upload,
    string ArchiveContentHash,
    string StagingPath,
    long Generation,
    IReadOnlyList<string> SelectedFragmentIds,
    IReadOnlyDictionary<string, string> InputValues,
    IReadOnlyDictionary<string, string> IdRemaps,
    IReadOnlyList<StackContributorBinding> Contributors,
    IReadOnlyList<RuntimeStackImportActionDescriptor> Actions,
    IReadOnlyList<RuntimeStackImportConflictDescriptor> Conflicts,
    DateTimeOffset ExpiresAtUtc);
