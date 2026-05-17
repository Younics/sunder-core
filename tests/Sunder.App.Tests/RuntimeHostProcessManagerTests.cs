using Sunder.App.Models;
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

    [Fact]
    public void CanReuseRunningRuntime_DoesNotReuseUnknownService()
    {
        var runningStatus = CreateStatus("Other.Service", "1.0.0");

        var canReuse = RuntimeHostProcessManager.CanReuseRunningRuntime(runningStatus, "1.0.0");

        Assert.False(canReuse);
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenUnknownServiceResponds_ThrowsAndDoesNotStartRuntime()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var runtimeUrl = new Uri("http://localhost:54321/");
        var startCount = 0;
        var manager = new RuntimeHostProcessManager(
            new AppStartupOptions(),
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeStatusAsync: (_, _) => Task.FromResult<SystemStatusResponse?>(CreateStatus("Other.Service", "1.0.0")),
            isRuntimeHealthyAsync: (_, _) => Task.FromResult(true),
            startProcess: _ => startCount++);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureStartedAsync(runtimeUrl));

            Assert.Contains("Other.Service", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, startCount);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenOnlyHealthEndpointResponds_ThrowsAndDoesNotStartRuntime()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var runtimeUrl = new Uri("http://localhost:54321/");
        var startCount = 0;
        var manager = new RuntimeHostProcessManager(
            new AppStartupOptions(),
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeStatusAsync: (_, _) => Task.FromResult<SystemStatusResponse?>(null),
            isRuntimeHealthyAsync: (_, _) => Task.FromResult(true),
            startProcess: _ => startCount++);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureStartedAsync(runtimeUrl));

            Assert.Contains("does not identify as Sunder.Runtime.Host", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, startCount);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenProcessStartFails_ThrowsUsefulError()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var runtimeUrl = new Uri("http://localhost:54321/");
        var manager = new RuntimeHostProcessManager(
            new AppStartupOptions(),
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeStatusAsync: (_, _) => Task.FromResult<SystemStatusResponse?>(null),
            isRuntimeHealthyAsync: (_, _) => Task.FromResult(false),
            startProcess: _ => throw new InvalidOperationException("start failed"));

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureStartedAsync(runtimeUrl));

            Assert.Contains("Failed to start Sunder.Runtime.Host", exception.Message, StringComparison.Ordinal);
            var innerException = Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Equal("start failed", innerException.Message);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenCalledConcurrently_StartsRuntimeOnce()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var runtimeHostPath = Path.Combine(rootPath, OperatingSystem.IsWindows() ? "Sunder.Runtime.Host.exe" : "Sunder.Runtime.Host");
        await File.WriteAllTextAsync(runtimeHostPath, string.Empty);
        var runtimeUrl = new Uri("http://localhost:54321/");
        var firstStatusProbeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstStatusProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimeStarted = false;
        var statusProbeCount = 0;
        var startCount = 0;
        var manager = new RuntimeHostProcessManager(
            new AppStartupOptions(),
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeStatusAsync: async (_, _) =>
            {
                var probeCount = Interlocked.Increment(ref statusProbeCount);
                if (probeCount == 1)
                {
                    firstStatusProbeStarted.SetResult();
                    await releaseFirstStatusProbe.Task;
                    return null;
                }

                return runtimeStarted ? CreateStatus("Sunder.Runtime.Host", "1.0.0") : null;
            },
            isRuntimeHealthyAsync: (_, _) => Task.FromResult(false),
            startProcess: _ =>
            {
                Interlocked.Increment(ref startCount);
                runtimeStarted = true;
            },
            delayAsync: (_, _) => Task.CompletedTask);

        try
        {
            var firstStart = manager.EnsureStartedAsync(runtimeUrl);
            await firstStatusProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var secondStart = manager.EnsureStartedAsync(runtimeUrl);

            releaseFirstStatusProbe.SetResult();
            await Task.WhenAll(firstStart, secondStart).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, startCount);
            Assert.True(statusProbeCount >= 3);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static SystemStatusResponse CreateStatus(string name, string version)
        => new(name, version, true, DateTimeOffset.UtcNow);

    private static async Task<(string RootPath, string RuntimeHostPath)> CreateRuntimeHostFileAsync()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var runtimeHostPath = Path.Combine(rootPath, OperatingSystem.IsWindows() ? "Sunder.Runtime.Host.exe" : "Sunder.Runtime.Host");
        await File.WriteAllTextAsync(runtimeHostPath, string.Empty);
        return (rootPath, runtimeHostPath);
    }
}
