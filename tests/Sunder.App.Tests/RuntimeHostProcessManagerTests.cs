using System.Diagnostics;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeHostProcessManagerTests
{
    [Theory]
    [InlineData("dev.sunder.runtime", 1, 1, 1, true)]
    [InlineData("dev.sunder.runtime", 2, 1, 2, true)]
    [InlineData("dev.sunder.runtime", 2, 2, 2, false)]
    [InlineData("dev.sunder.runtime", 0, 0, 0, false)]
    [InlineData("other.runtime", 1, 1, 1, false)]
    public void CanReuseRunningRuntime_UsesProtocolIdentityAndRange(
        string identity,
        int revision,
        int minimum,
        int maximum,
        bool expected)
    {
        var handshake = CreateHandshake(identity, revision, minimum, maximum);

        Assert.Equal(expected, RuntimeHostProcessManager.CanReuseRunningRuntime(handshake));
    }

    [Fact]
    public void CanReuseRunningRuntime_RejectsMissingRequiredFeature()
    {
        var handshake = CreateHandshake() with { SupportedFeatures = [] };

        Assert.False(RuntimeHostProcessManager.CanReuseRunningRuntime(handshake));
    }

    [Fact]
    public void ShouldReplaceRunningRuntime_ReplacesOnlyKnownIncompatibleProtocol()
    {
        Assert.True(RuntimeHostProcessManager.ShouldReplaceRunningRuntime(
            CreateHandshake(revision: 2, minimum: 2, maximum: 2)));
        Assert.False(RuntimeHostProcessManager.ShouldReplaceRunningRuntime(
            CreateHandshake(identity: "other.runtime")));
        Assert.False(RuntimeHostProcessManager.ShouldReplaceRunningRuntime(null));
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenUnknownServiceResponds_ThrowsAndDoesNotStartRuntime()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var startCount = 0;
        var manager = new RuntimeHostProcessManager(
            new AppStartupOptions(),
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(CreateHandshake("other.runtime")),
            isRuntimeHealthyAsync: (_, _) => Task.FromResult(true),
            startProcess: _ => startCount++);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureStartedAsync(new Uri("http://localhost:54321/")));

            Assert.Contains("Other.Runtime", exception.Message, StringComparison.Ordinal);
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
        var startCount = 0;
        var manager = new RuntimeHostProcessManager(
            new AppStartupOptions(),
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(null),
            isRuntimeHealthyAsync: (_, _) => Task.FromResult(true),
            startProcess: _ => startCount++);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureStartedAsync(new Uri("http://localhost:54321/")));

            Assert.Contains("does not identify as Sunder.Runtime.Host", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, startCount);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenManagedUrlIsNotLoopback_RejectsBeforeLaunch()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var startCount = 0;
        var manager = new RuntimeHostProcessManager(
            new AppStartupOptions(),
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(null),
            isRuntimeHealthyAsync: (_, _) => Task.FromResult(false),
            startProcess: _ => startCount++,
            connectionInfoPath: Path.Combine(rootPath, "connection-v1.json"));

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureStartedAsync(new Uri("http://192.0.2.1:5275/")));
            Assert.Equal(0, startCount);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenDevPackagesConfigured_PassesSecretsOutOfArguments()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var runtimeUrl = new Uri("http://localhost:54321/");
        var devPackageFolder = Path.Combine(rootPath, "dev package");
        var connectionInfoPath = Path.Combine(rootPath, "connection-v1.json");
        ProcessStartInfo? capturedStartInfo = null;
        var runtimeStarted = false;
        var manager = new RuntimeHostProcessManager(
            new AppStartupOptions { DevPackageFolders = [devPackageFolder] },
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                runtimeStarted ? CreateHandshake() : null),
            isRuntimeHealthyAsync: (_, _) => Task.FromResult(false),
            startProcess: startInfo =>
            {
                capturedStartInfo = startInfo;
                runtimeStarted = true;
            },
            delayAsync: (_, _) => Task.CompletedTask,
            connectionInfoPath: connectionInfoPath);

        try
        {
            await manager.EnsureStartedAsync(runtimeUrl);

            Assert.NotNull(capturedStartInfo);
            Assert.Contains("--dev-package", capturedStartInfo.ArgumentList);
            Assert.Contains(devPackageFolder, capturedStartInfo.ArgumentList);
            Assert.True(capturedStartInfo.Environment.TryGetValue("SUNDER_RUNTIME_BEARER_TOKEN", out var bearerToken));
            Assert.False(string.IsNullOrWhiteSpace(bearerToken));
            Assert.DoesNotContain(capturedStartInfo.ArgumentList, argument => string.Equals(argument, bearerToken, StringComparison.Ordinal));
            Assert.Equal(connectionInfoPath, capturedStartInfo.Environment["SUNDER_RUNTIME_CONNECTION_FILE"]);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenCalledConcurrently_StartsRuntimeOnce()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var runtimeUrl = new Uri("http://localhost:54321/");
        var connectionInfoPath = Path.Combine(rootPath, "connection-v1.json");
        var firstProbeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimeStarted = false;
        var probeCount = 0;
        var startCount = 0;
        var manager = new RuntimeHostProcessManager(
            new AppStartupOptions(),
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeHandshakeAsync: async (_, _) =>
            {
                if (Interlocked.Increment(ref probeCount) == 1)
                {
                    firstProbeStarted.SetResult();
                    await releaseFirstProbe.Task;
                    return null;
                }
                return runtimeStarted ? CreateHandshake() : null;
            },
            isRuntimeHealthyAsync: (_, _) => Task.FromResult(false),
            startProcess: _ =>
            {
                Interlocked.Increment(ref startCount);
                runtimeStarted = true;
            },
            delayAsync: (_, _) => Task.CompletedTask,
            connectionInfoPath: connectionInfoPath);

        try
        {
            var first = manager.EnsureStartedAsync(runtimeUrl);
            await firstProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = manager.EnsureStartedAsync(runtimeUrl);
            releaseFirstProbe.SetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, startCount);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static RuntimeHandshakeResponse CreateHandshake(
        string identity = RuntimeProtocol.Identity,
        int revision = RuntimeProtocol.CurrentRevision,
        int minimum = RuntimeProtocol.MinimumSupportedRevision,
        int maximum = RuntimeProtocol.MaximumSupportedRevision)
        => new(
            identity,
            revision,
            minimum,
            maximum,
            Guid.NewGuid(),
            [RuntimeProtocolFeatures.VersionedApiV1],
            new RuntimeProductVersionDiagnostics("Other.Runtime", "not-a-protocol-version", "diagnostic-build"));

    private static async Task<(string RootPath, string RuntimeHostPath)> CreateRuntimeHostFileAsync()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var runtimeHostPath = Path.Combine(rootPath, OperatingSystem.IsWindows() ? "Sunder.Runtime.Host.exe" : "Sunder.Runtime.Host");
        await File.WriteAllTextAsync(runtimeHostPath, string.Empty);
        return (rootPath, runtimeHostPath);
    }
}
