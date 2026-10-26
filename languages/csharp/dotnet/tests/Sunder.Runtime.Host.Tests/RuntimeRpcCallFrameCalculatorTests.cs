using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeRpcCallFrameCalculatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);

    private static readonly RuntimeRpcPolicyOptions Policy = new()
    {
        DefaultDeadline = TimeSpan.FromMinutes(5),
        MaximumExtendedDeadline = TimeSpan.FromMinutes(35),
    };

    [Fact]
    public void Merge_PreservesSingleFrameAndCombinesMaximumDepthWithEarliestDeadline()
    {
        var first = new RuntimeRpcCallFrame(2, Now.AddMinutes(10));
        var second = new RuntimeRpcCallFrame(5, Now.AddMinutes(20)) { RecentCalls = ["package/service.method"] };

        Assert.Null(RuntimeRpcCallFrameCalculator.Merge(null, null));
        Assert.Same(first, RuntimeRpcCallFrameCalculator.Merge(first, null));
        Assert.Same(second, RuntimeRpcCallFrameCalculator.Merge(null, second));

        var merged = Assert.IsType<RuntimeRpcCallFrame>(
            RuntimeRpcCallFrameCalculator.Merge(first, second));
        Assert.Equal(5, merged.Depth);
        Assert.Equal(Now.AddMinutes(10), merged.DeadlineUtc);
        Assert.Equal(second.RecentCalls, merged.RecentCalls);
        Assert.Equal(second.RecentCalls, RuntimeRpcCallFrameCalculator.Merge(second, first)!.RecentCalls);
    }

    [Fact]
    public void NestingDiagnostics_KeepOnlyBoundedSanitizedRecentCallsWithinWorkerMessageLimit()
    {
        RuntimeRpcCallFrame? parent = null;
        for (var depth = 1; depth <= 128; depth++)
        {
            parent = RuntimeRpcCallFrameCalculator.CreateChild(parent, depth, Now.AddMinutes(5),
                $"package{depth}/service.method\n{new string('x', 256)}");
            Assert.InRange(parent.RecentCalls.Count, 1, 4);
            Assert.All(parent.RecentCalls, call =>
            {
                Assert.InRange(call.Length, 1, 88);
                Assert.DoesNotContain('\n', call);
            });
        }

        var message = RuntimeRpcCallFrameCalculator.DescribeNestingLimit(parent, 129, 128, "next/service.method");
        Assert.Contains("depth 129", message);
        Assert.Contains("limit is 128", message);
        Assert.Contains("package128/service.method", message);
        Assert.DoesNotContain("package125/", message);
        Assert.EndsWith("next/service.method", message);
        Assert.InRange(message.Length, 1, 512);
    }

    [Fact]
    public void CalculateScopeDeadline_AppliesDefaultMaximumAndParentPrecedence()
    {
        Assert.Equal(
            Now.AddMinutes(5),
            RuntimeRpcCallFrameCalculator.CalculateScopeDeadline(Policy, null, null, Now));
        Assert.Equal(
            Now.AddMinutes(30),
            RuntimeRpcCallFrameCalculator.CalculateScopeDeadline(
                Policy,
                new SunderRpcCallOptions(Now.AddMinutes(30)),
                null,
                Now));
        Assert.Equal(
            Now.AddMinutes(35),
            RuntimeRpcCallFrameCalculator.CalculateScopeDeadline(
                Policy,
                new SunderRpcCallOptions(Now.AddHours(1)),
                null,
                Now));
        Assert.Equal(
            Now.AddMinutes(3),
            RuntimeRpcCallFrameCalculator.CalculateScopeDeadline(
                Policy,
                new SunderRpcCallOptions(Now.AddMinutes(30)),
                new RuntimeRpcCallFrame(2, Now.AddMinutes(3)),
                Now));
        Assert.Equal(
            Now,
            RuntimeRpcCallFrameCalculator.CalculateScopeDeadline(
                Policy,
                new SunderRpcCallOptions(Now),
                null,
                Now));
    }

    [Fact]
    public void CalculateInvocationDeadline_AppliesCapabilityParentAndScopePrecedence()
    {
        Assert.Equal(
            Now.AddMinutes(5),
            RuntimeRpcCallFrameCalculator.CalculateInvocationDeadline(
                Policy,
                new SunderRpcCallOptions(Now.AddMinutes(30)),
                null,
                Now,
                targetSupportsExtendedDeadline: false));
        Assert.Equal(
            Now.AddMinutes(35),
            RuntimeRpcCallFrameCalculator.CalculateInvocationDeadline(
                Policy,
                new SunderRpcCallOptions(Now.AddHours(1)),
                null,
                Now,
                targetSupportsExtendedDeadline: true));
        Assert.Equal(
            Now.AddMinutes(3),
            RuntimeRpcCallFrameCalculator.CalculateInvocationDeadline(
                Policy,
                new SunderRpcCallOptions(Now.AddMinutes(30)),
                new RuntimeRpcCallFrame(2, Now.AddMinutes(3)),
                Now,
                targetSupportsExtendedDeadline: true));
        Assert.Equal(
            Now.AddMinutes(2),
            RuntimeRpcCallFrameCalculator.CalculateInvocationDeadline(
                Policy,
                new SunderRpcCallOptions(Now.AddMinutes(30)),
                new RuntimeRpcCallFrame(2, Now.AddMinutes(3)),
                Now,
                targetSupportsExtendedDeadline: true,
                scopeDeadline: Now.AddMinutes(2)));
        Assert.Equal(
            Now.AddMinutes(30),
            RuntimeRpcCallFrameCalculator.CalculateInvocationDeadline(
                Policy,
                options: null,
                parent: null,
                Now,
                targetSupportsExtendedDeadline: true,
                scopeDeadline: Now.AddMinutes(30)));
        Assert.Equal(
            Now,
            RuntimeRpcCallFrameCalculator.CalculateInvocationDeadline(
                Policy,
                new SunderRpcCallOptions(Now),
                parent: null,
                Now,
                targetSupportsExtendedDeadline: true));
    }
}
