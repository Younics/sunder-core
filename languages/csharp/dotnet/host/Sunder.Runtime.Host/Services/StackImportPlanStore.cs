using Sunder.Runtime.Contracts;
using Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Services;

internal sealed class StackImportPlanStore(
    RuntimeContentTransferStore transfers,
    TimeProvider timeProvider,
    RuntimeStackPolicyOptions policy) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StackImportPlan> _plans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StackImportPlanReservation> _ownedPlans = new(StringComparer.Ordinal);
    private long _extractedBytes;

    public DateTimeOffset ExpiresAtUtc => timeProvider.GetUtcNow() + policy.ImportPlanLifetime;

    public bool TryAdd(StackImportPlan plan)
    {
        if (!TryMeasureExtractedBytes(plan.StagingPath, out var extractedBytes)) return false;
        lock (_gate)
        {
            if (_ownedPlans.Count >= policy.MaxImportPlans
                || WouldExceed(_extractedBytes, extractedBytes, policy.MaxImportPlanExtractedBytes)
                || _ownedPlans.ContainsKey(plan.PlanId))
            {
                return false;
            }

            _plans.Add(plan.PlanId, plan);
            _ownedPlans.Add(plan.PlanId, new StackImportPlanReservation(plan, extractedBytes));
            _extractedBytes += extractedBytes;
            return true;
        }
    }

    public bool TryTake(string planId, out StackImportPlan plan)
    {
        lock (_gate)
        {
            return _plans.Remove(planId, out plan!);
        }
    }

    public bool Discard(string planId)
    {
        if (!TryTake(planId, out var plan)) return false;
        Release(plan);
        return true;
    }

    public void SweepExpired()
    {
        var now = timeProvider.GetUtcNow();
        StackImportPlan[] expired;
        lock (_gate)
        {
            expired = _plans.Values.Where(plan => plan.ExpiresAtUtc <= now).ToArray();
            foreach (var plan in expired) _plans.Remove(plan.PlanId);
        }
        foreach (var plan in expired) Release(plan);
    }

    public bool IsExpired(StackImportPlan plan) => plan.ExpiresAtUtc <= timeProvider.GetUtcNow();

    public void Release(StackImportPlan plan)
    {
        lock (_gate)
        {
            if (_ownedPlans.TryGetValue(plan.PlanId, out var reservation)
                && ReferenceEquals(reservation.Plan, plan))
            {
                _ownedPlans.Remove(plan.PlanId);
                _extractedBytes -= reservation.ExtractedBytes;
            }
        }
        TryDeleteDirectory(plan.StagingPath);
        transfers.ReleaseUpload(plan.Upload);
    }

    public void Dispose()
    {
        StackImportPlan[] plans;
        lock (_gate)
        {
            plans = _ownedPlans.Values.Select(static reservation => reservation.Plan).ToArray();
            _plans.Clear();
            _ownedPlans.Clear();
            _extractedBytes = 0;
        }
        foreach (var plan in plans)
        {
            TryDeleteDirectory(plan.StagingPath);
            transfers.ReleaseUpload(plan.Upload);
        }
    }

    private bool TryMeasureExtractedBytes(string path, out long extractedBytes)
    {
        extractedBytes = 0;
        try
        {
            foreach (var filePath in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var length = new FileInfo(filePath).Length;
                if (WouldExceed(extractedBytes, length, policy.MaxImportPlanExtractedBytes))
                {
                    return false;
                }
                extractedBytes += length;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool WouldExceed(long current, long additional, long limit)
        => additional < 0 || additional > limit || current > limit - additional;

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

    private sealed record StackImportPlanReservation(StackImportPlan Plan, long ExtractedBytes);
}

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
