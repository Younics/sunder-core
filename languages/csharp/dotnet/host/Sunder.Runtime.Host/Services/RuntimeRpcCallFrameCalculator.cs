using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimeRpcCallFrame(int Depth, DateTimeOffset DeadlineUtc);

internal static class RuntimeRpcCallFrameCalculator
{
    public static RuntimeRpcCallFrame? Merge(
        RuntimeRpcCallFrame? first,
        RuntimeRpcCallFrame? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        return new RuntimeRpcCallFrame(
            Math.Max(first.Depth, second.Depth),
            first.DeadlineUtc < second.DeadlineUtc ? first.DeadlineUtc : second.DeadlineUtc);
    }

    public static DateTimeOffset CalculateScopeDeadline(
        RuntimeRpcPolicyOptions policy,
        SunderRpcCallOptions? options,
        RuntimeRpcCallFrame? parent,
        DateTimeOffset now)
    {
        var defaultDeadline = now + policy.DefaultDeadline;
        var maximumDeadline = now + policy.MaximumExtendedDeadline;
        var deadline = options?.DeadlineUtc is { } requestedDeadline
            ? requestedDeadline < maximumDeadline ? requestedDeadline : maximumDeadline
            : defaultDeadline;
        if (parent is not null && parent.DeadlineUtc < deadline) deadline = parent.DeadlineUtc;
        return deadline;
    }

    public static DateTimeOffset CalculateInvocationDeadline(
        RuntimeRpcPolicyOptions policy,
        SunderRpcCallOptions? options,
        RuntimeRpcCallFrame? parent,
        DateTimeOffset now,
        bool targetSupportsExtendedDeadline,
        DateTimeOffset? scopeDeadline = null)
    {
        var defaultDeadline = now + policy.DefaultDeadline;
        var maximumDeadline = targetSupportsExtendedDeadline
            ? now + policy.MaximumExtendedDeadline
            : defaultDeadline;
        var requestedDeadline = options?.DeadlineUtc ?? scopeDeadline;
        var deadline = requestedDeadline is { } requested
            ? requested < maximumDeadline ? requested : maximumDeadline
            : defaultDeadline;
        if (parent is not null && parent.DeadlineUtc < deadline) deadline = parent.DeadlineUtc;
        if (scopeDeadline is { } scoped && scoped < deadline) deadline = scoped;
        return deadline;
    }
}
