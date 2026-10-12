using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

/// <summary>Bounded, expiring consent previews; durable prepared transitions live in management state.</summary>
internal sealed class ExecutionRuntimeManagementPlans(TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (RuntimeExecutionRuntimePlanIdentity Plan, object Value)> _plans = new(StringComparer.Ordinal);
    internal RuntimeExecutionRuntimePlanIdentity Add<T>(T value, DateTimeOffset? expires = null) where T : notnull
    {
        lock (_gate)
        {
            foreach (var stale in _plans.Where(pair => pair.Value.Plan.ExpiresAtUtc <= clock.GetUtcNow()).Select(pair => pair.Key).ToArray()) _plans.Remove(stale);
            if (_plans.Count >= 128) throw new RuntimeConflictException("Too many runtime previews; wait for existing previews to expire.");
            var deadline = clock.GetUtcNow().AddMinutes(5);
            if (expires is { } expiry && expiry < deadline) deadline = expiry;
            var plan = new RuntimeExecutionRuntimePlanIdentity(Guid.NewGuid().ToString("N"), ExecutionRuntimeManagementSupport.Hash(value), deadline, ExecutionRuntimeManagementSupport.Rid);
            _plans.Add(plan.PlanId, (plan, value));
            return plan;
        }
    }
    internal T Get<T>(RuntimeExecutionRuntimePlanConfirmation confirmation) where T : notnull
    {
        lock (_gate)
        {
            if (confirmation is null || !_plans.TryGetValue(confirmation.PlanId, out var entry) || entry.Plan.Fingerprint != confirmation.ExpectedFingerprint
                || entry.Plan.ExpiresAtUtc <= clock.GetUtcNow() || entry.Value is not T value) throw ExecutionRuntimeManagementSupport.Stale();
            return value;
        }
    }
}
