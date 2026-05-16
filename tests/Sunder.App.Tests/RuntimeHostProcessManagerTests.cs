using Sunder.App.Services;
using Sunder.Protocol;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeHostProcessManagerTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.1.0", "1.0.9", 1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("1.0.0", "1.0.0-beta.1", 1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.1", 1)]
    [InlineData("1.0.0-beta.1", "1.0.0", -1)]
    [InlineData("1.0.0+build.2", "1.0.0+build.1", 0)]
    public void VersionComparer_OrdersSemVerValues(string left, string right, int expectedSign)
    {
        var parsed = RuntimeHostVersionComparer.TryCompare(left, right, out var comparison);

        Assert.True(parsed);
        Assert.Equal(expectedSign, Math.Sign(comparison));
    }

    [Fact]
    public void VersionComparer_InvalidVersionReturnsFalse()
    {
        var parsed = RuntimeHostVersionComparer.TryCompare("Development", "1.0.0", out _);

        Assert.False(parsed);
    }

    [Theory]
    [InlineData("0.9.0", "1.0.0", true)]
    [InlineData("1.0.0-beta.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.1.0", "1.0.0", false)]
    public void ShouldReplaceRunningRuntime_OnlyReplacesOlderSunderHost(
        string runningVersion,
        string bundledVersion,
        bool expected)
    {
        var runningStatus = CreateStatus("Sunder.Runtime.Host", runningVersion);

        var shouldReplace = RuntimeHostProcessManager.ShouldReplaceRunningRuntime(
            runningStatus,
            bundledVersion);

        Assert.Equal(expected, shouldReplace);
    }

    [Fact]
    public void ShouldReplaceRunningRuntime_DoesNotReplaceUnknownService()
    {
        var runningStatus = CreateStatus("Other.Service", "0.1.0");

        var shouldReplace = RuntimeHostProcessManager.ShouldReplaceRunningRuntime(
            runningStatus,
            "1.0.0");

        Assert.False(shouldReplace);
    }

    [Fact]
    public void CanReuseRunningRuntime_ReusesSameOrNewerSunderHost()
    {
        var runningStatus = CreateStatus("Sunder.Runtime.Host", "1.1.0");

        var canReuse = RuntimeHostProcessManager.CanReuseRunningRuntime(runningStatus, "1.0.0");

        Assert.True(canReuse);
    }

    [Fact]
    public void CanReuseRunningRuntime_ReusesHostWhenVersionCannotBeCompared()
    {
        var runningStatus = CreateStatus("Sunder.Runtime.Host", "Development");

        var canReuse = RuntimeHostProcessManager.CanReuseRunningRuntime(runningStatus, "1.0.0");

        Assert.True(canReuse);
    }

    [Fact]
    public void CanReuseRunningRuntime_DoesNotReuseOlderSunderHost()
    {
        var runningStatus = CreateStatus("Sunder.Runtime.Host", "0.9.0");

        var canReuse = RuntimeHostProcessManager.CanReuseRunningRuntime(runningStatus, "1.0.0");

        Assert.False(canReuse);
    }

    private static SystemStatusResponse CreateStatus(string name, string version)
        => new(name, version, true, DateTimeOffset.UtcNow);
}
