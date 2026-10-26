using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimeRpcCallFrame(int Depth, DateTimeOffset DeadlineUtc)
{
    // Host-only, bounded diagnostic tail. Never includes arguments or content.
    public IReadOnlyList<string> RecentCalls { get; init; } = [];
}

internal static class RuntimeRpcCallFrameCalculator
{
    private const int MaximumRecentCalls = 4;
    private const int MaximumCallSiteCharacters = 88;

    public static RuntimeRpcCallFrame? Merge(
        RuntimeRpcCallFrame? first,
        RuntimeRpcCallFrame? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        var deepest = first.Depth >= second.Depth ? first : second;
        return deepest with
        {
            DeadlineUtc = first.DeadlineUtc < second.DeadlineUtc ? first.DeadlineUtc : second.DeadlineUtc,
        };
    }

    public static RuntimeRpcCallFrame CreateChild(
        RuntimeRpcCallFrame? parent, int depth, DateTimeOffset deadline, string? callSite)
        => new(depth, deadline) { RecentCalls = AppendCallSite(parent, callSite) };

    public static string DescribeNestingLimit(
        RuntimeRpcCallFrame? parent, long depth, int limit, string? callSite)
    {
        var message = $"The RPC call chain reached nesting depth {depth}; the limit is {limit}.";
        var recent = AppendCallSite(parent, callSite);
        return recent.Count == 0 ? message : $"{message} Recent calls: {string.Join(" -> ", recent)}";
    }

    private static IReadOnlyList<string> AppendCallSite(RuntimeRpcCallFrame? parent, string? callSite)
    {
        var recent = parent?.RecentCalls ?? [];
        if (callSite is null) return recent;
        var bounded = new string(callSite.Take(MaximumCallSiteCharacters)
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '/'
                ? character : '_').ToArray());
        return recent.TakeLast(MaximumRecentCalls - 1).Append(bounded).ToArray();
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
