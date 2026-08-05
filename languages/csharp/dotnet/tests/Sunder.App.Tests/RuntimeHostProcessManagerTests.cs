using System.Diagnostics;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Host.Client;
using Sunder.Host.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeHostProcessManagerTests
{
    [Theory]
    [InlineData("dev.sunder.runtime", 1, 1, 1, false)]
    [InlineData("dev.sunder.runtime", 2, 1, 2, false)]
    [InlineData("dev.sunder.runtime", 4, 4, 4, false)]
    [InlineData("dev.sunder.runtime", 5, 5, 5, true)]
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
    public void CanReuseRunningRuntime_RequiresBaseFeatureAndLeavesAtomicSnapshotOperationScoped()
    {
        var handshake = CreateHandshake() with { SupportedFeatures = [] };

        Assert.False(RuntimeHostProcessManager.CanReuseRunningRuntime(handshake));
        var versionedApiOnly = CreateHandshake() with
        {
            SupportedFeatures = [RuntimeProtocolFeatures.VersionedApiV1],
        };
        Assert.True(RuntimeHostProcessManager.CanReuseRunningRuntime(versionedApiOnly));
        Assert.Contains(
            RuntimeProtocolFeatures.AtomicPackageSnapshotV1,
            RuntimeProtocolCompatibility.GetIncompatibility(
                versionedApiOnly,
                RuntimeProtocolFeatures.AtomicPackageSnapshotV1),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldReplaceRunningRuntime_ReplacesOnlyKnownIncompatibleProtocol()
    {
        Assert.True(RuntimeHostProcessManager.ShouldReplaceRunningRuntime(
            CreateHandshake(revision: 1, minimum: 1, maximum: 1)));
        Assert.False(RuntimeHostProcessManager.ShouldReplaceRunningRuntime(
            CreateHandshake(identity: "other.runtime")));
        Assert.False(RuntimeHostProcessManager.ShouldReplaceRunningRuntime(null));
    }

    [Fact]
    public async Task EnsureStartedAsync_ObservesLaunchReceiptAndLogsServiceIdentity()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var runtimeUrl = new Uri("http://127.0.0.1:54321/");
        var serviceName = $"test-service-{Guid.NewGuid():N}";
        var receipt = new HostServiceLaunchReceipt("test-backend", serviceName, 42001);
        var runtimeReady = false;
        var hostServiceManager = new TestHostServiceManager(
            receipt,
            (_, _) =>
            {
                runtimeReady = true;
                return Task.FromResult(new HostServiceObservation(HostServiceState.Starting, 42001));
            });

        try
        {
            using var manager = CreateObservedLaunchManager(
                runtimeHostPath,
                Path.Combine(rootPath, "connection.json"),
                hostServiceManager,
                (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                    runtimeReady ? CreateHandshake() : null));

            await manager.EnsureStartedAsync(runtimeUrl);

            Assert.Equal(1, hostServiceManager.LaunchCount);
            Assert.Same(receipt, hostServiceManager.ObservedReceipt);
            Assert.Equal(runtimeHostPath, hostServiceManager.StartInfo?.FileName);
            Assert.Contains(
                AppSessionLog.Snapshot(),
                entry => entry.Message.Contains("backend=test-backend", StringComparison.Ordinal)
                         && entry.Message.Contains($"service={serviceName}", StringComparison.Ordinal)
                         && entry.Message.Contains("pid=42001", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenObservedProcessExits_FailsImmediatelyWithSafeTypedError()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var receipt = new HostServiceLaunchReceipt("direct", "sunder-host", 42002);
        var delayCount = 0;
        var hostServiceManager = new TestHostServiceManager(
            receipt,
            (_, _) => Task.FromResult(new HostServiceObservation(
                HostServiceState.Failed,
                processId: 42002,
                exitCode: 17,
                result: "process-exit",
                safeDetail: "password=do-not-expose",
                diagnosticPaths: [Path.Combine(rootPath, "host.log")])));

        try
        {
            using var manager = CreateObservedLaunchManager(
                runtimeHostPath,
                Path.Combine(rootPath, "connection.json"),
                hostServiceManager,
                static (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(null),
                delayAsync: (_, _) =>
                {
                    delayCount++;
                    return Task.CompletedTask;
                });

            var exception = await Assert.ThrowsAsync<HostServiceStartupException>(
                () => manager.EnsureStartedAsync(new Uri("http://127.0.0.1:54321/")));

            Assert.Equal(HostServiceStartupFailure.TerminalState, exception.Failure);
            Assert.Same(receipt, exception.Receipt);
            Assert.Equal(HostServiceState.Failed, exception.Observation?.State);
            Assert.Equal(17, exception.Observation?.ExitCode);
            Assert.Contains("Exit code: 17", exception.Message, StringComparison.Ordinal);
            Assert.Contains("password=[redacted]", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("do-not-expose", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, delayCount);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenServiceObservationThrows_ReturnsSafeTypedError()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var receipt = new HostServiceLaunchReceipt("test-backend", "sunder-host", 42003);
        var delayCount = 0;
        var hostServiceManager = new TestHostServiceManager(
            receipt,
            static (_, _) => throw new InvalidOperationException("access_token=do-not-expose"));

        try
        {
            using var manager = CreateObservedLaunchManager(
                runtimeHostPath,
                Path.Combine(rootPath, "connection.json"),
                hostServiceManager,
                static (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(null),
                delayAsync: (_, _) =>
                {
                    delayCount++;
                    return Task.CompletedTask;
                });

            var exception = await Assert.ThrowsAsync<HostServiceStartupException>(
                () => manager.EnsureStartedAsync(new Uri("http://127.0.0.1:54321/")));

            Assert.Equal(HostServiceStartupFailure.ObservationError, exception.Failure);
            Assert.Null(exception.Observation);
            Assert.Contains("access_token=[redacted]", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("do-not-expose", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, delayCount);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenServiceObservationIsUnknown_WaitsUntilTimeout()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var receipt = new HostServiceLaunchReceipt("legacy-test", "sunder-host");
        var hostServiceManager = new TestHostServiceManager(
            receipt,
            static (_, _) => Task.FromResult(HostServiceObservation.Unknown()));
        var timeProvider = new ManualTimeProvider();
        var delayCount = 0;

        try
        {
            using var manager = CreateObservedLaunchManager(
                runtimeHostPath,
                Path.Combine(rootPath, "connection.json"),
                hostServiceManager,
                static (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(null),
                delayAsync: (delay, _) =>
                {
                    delayCount++;
                    timeProvider.Advance(delay);
                    return Task.CompletedTask;
                },
                timeProvider,
                TimeSpan.FromMilliseconds(800));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureStartedAsync(new Uri("http://127.0.0.1:54321/")));

            Assert.IsNotType<HostServiceStartupException>(exception);
            Assert.Contains("within 1 seconds", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, hostServiceManager.LaunchCount);
            Assert.Equal(3, hostServiceManager.ObserveCount);
            Assert.Equal(2, delayCount);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenObservedServiceRunsAndConnectionAppears_Succeeds()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var receipt = new HostServiceLaunchReceipt("test-backend", "sunder-host", 42004);
        var runtimeReady = false;
        var hostServiceManager = new TestHostServiceManager(
            receipt,
            static (_, _) => Task.FromResult(new HostServiceObservation(
                HostServiceState.Running,
                processId: 42004)));
        var delayCount = 0;

        try
        {
            using var manager = CreateObservedLaunchManager(
                runtimeHostPath,
                Path.Combine(rootPath, "connection.json"),
                hostServiceManager,
                (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                    runtimeReady ? CreateHandshake() : null),
                delayAsync: (_, _) =>
                {
                    delayCount++;
                    runtimeReady = true;
                    return Task.CompletedTask;
                });

            await manager.EnsureStartedAsync(new Uri("http://127.0.0.1:54321/"));

            Assert.Equal(1, hostServiceManager.LaunchCount);
            Assert.Equal(1, hostServiceManager.ObserveCount);
            Assert.Equal(1, delayCount);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenReplacingRuntime_WaitsForStateLeaseRelease()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var runtimeUrl = new Uri("http://localhost:54321/");
        var connectionInfoPath = Path.Combine(rootPath, "connection-v1.json");
        RuntimeConnectionInfoStore.Save(
            new RuntimeConnectionInfo(runtimeUrl, "existing-token"),
            connectionInfoPath);
        var runtimeRunning = true;
        var leaseAvailable = false;
        var replacementStarted = false;
        var delayCount = 0;
        var manager = new RuntimeHostProcessManager(
            new AppStartupOptions(),
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                replacementStarted
                    ? CreateHandshake()
                    : runtimeRunning
                        ? CreateHandshake(revision: 1, minimum: 1, maximum: 1)
                        : null),
            isRuntimeHealthyAsync: (_, _) => Task.FromResult(runtimeRunning),
            shutdownRuntimeAsync: (_, _) =>
            {
                runtimeRunning = false;
                return Task.CompletedTask;
            },
            hostServiceManager: new StartProcessHostServiceManager(_ =>
            {
                Assert.True(leaseAvailable);
                replacementStarted = true;
            }),
            delayAsync: (_, _) =>
            {
                if (Interlocked.Increment(ref delayCount) == 2)
                {
                    leaseAvailable = true;
                }
                return Task.CompletedTask;
            },
            connectionInfoPath: connectionInfoPath,
            isRuntimeLeaseAvailable: () => leaseAvailable);

        try
        {
            await manager.EnsureStartedAsync(runtimeUrl);

            Assert.True(replacementStarted);
            Assert.Equal(2, delayCount);
        }
        finally
        {
            manager.Dispose();
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenSameUrlRuntimeIsShuttingDown_WaitsForStateLeaseRelease()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var runtimeUrl = new Uri("http://localhost:54321/");
        var connectionInfoPath = Path.Combine(rootPath, "connection-v1.json");
        RuntimeConnectionInfoStore.Save(
            new RuntimeConnectionInfo(runtimeUrl, "stopping-token"),
            connectionInfoPath);
        var leaseAvailable = false;
        var replacementStarted = false;
        var delayCount = 0;
        var manager = new RuntimeHostProcessManager(
            new AppStartupOptions(),
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                replacementStarted ? CreateHandshake() : null),
            isRuntimeHealthyAsync: (_, _) => Task.FromResult(false),
            hostServiceManager: new StartProcessHostServiceManager(_ =>
            {
                Assert.True(leaseAvailable);
                replacementStarted = true;
            }),
            delayAsync: (_, _) =>
            {
                if (Interlocked.Increment(ref delayCount) == 2)
                {
                    leaseAvailable = true;
                }
                return Task.CompletedTask;
            },
            connectionInfoPath: connectionInfoPath,
            isRuntimeLeaseAvailable: () => leaseAvailable);

        try
        {
            await manager.EnsureStartedAsync(runtimeUrl);

            Assert.True(replacementStarted);
            Assert.Equal(2, delayCount);
        }
        finally
        {
            manager.Dispose();
            Directory.Delete(rootPath, recursive: true);
        }
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
            hostServiceManager: new StartProcessHostServiceManager(_ => startCount++),
            connectionInfoPath: Path.Combine(rootPath, "connection-v1.json"));

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
            hostServiceManager: new StartProcessHostServiceManager(_ => startCount++),
            connectionInfoPath: Path.Combine(rootPath, "connection-v1.json"));

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
            hostServiceManager: new StartProcessHostServiceManager(_ => startCount++),
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
    public async Task EnsureStartedAsync_WhenDevPackagesConfigured_DoesNotPassDevFoldersToRuntime()
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
            hostServiceManager: new StartProcessHostServiceManager(startInfo =>
            {
                Assert.Null(RuntimeConnectionInfoStore.Load(connectionInfoPath));
                capturedStartInfo = startInfo;
                runtimeStarted = true;
            }),
            delayAsync: (_, _) => Task.CompletedTask,
            connectionInfoPath: connectionInfoPath,
            isRuntimeLeaseAvailable: () => true);

        try
        {
            await manager.EnsureStartedAsync(runtimeUrl);

            Assert.NotNull(capturedStartInfo);
            Assert.DoesNotContain("--dev-package", capturedStartInfo.ArgumentList);
            Assert.DoesNotContain(devPackageFolder, capturedStartInfo.ArgumentList);
            Assert.False(capturedStartInfo.Environment.ContainsKey("SUNDER_RUNTIME_BEARER_TOKEN"));
            Assert.Equal(connectionInfoPath, capturedStartInfo.Environment["SUNDER_RUNTIME_CONNECTION_FILE"]);
            Assert.Null(RuntimeConnectionInfoStore.Load(connectionInfoPath));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenDevPackagesConfigured_ReusesWarmManagedRuntime()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var runtimeUrl = new Uri("http://localhost:54321/");
        var connectionInfoPath = Path.Combine(rootPath, "connection-v1.json");
        RuntimeConnectionInfoStore.Save(new RuntimeConnectionInfo(runtimeUrl, "old-managed-token"), connectionInfoPath);
        var runtimeRunning = true;
        var shutdownCount = 0;
        var startCount = 0;
        var manager = new RuntimeHostProcessManager(
            new AppStartupOptions { DevPackageFolders = [Path.Combine(rootPath, "dev-package")] },
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(runtimeRunning ? CreateHandshake() : null),
            isRuntimeHealthyAsync: (_, _) => Task.FromResult(runtimeRunning),
            shutdownRuntimeAsync: (_, _) =>
            {
                shutdownCount++;
                runtimeRunning = false;
                return Task.CompletedTask;
            },
            hostServiceManager: new StartProcessHostServiceManager(_ =>
            {
                startCount++;
                runtimeRunning = true;
            }),
            delayAsync: (_, _) => Task.CompletedTask,
            connectionInfoPath: connectionInfoPath,
            isRuntimeLeaseAvailable: () => true);

        try
        {
            await manager.EnsureStartedAsync(runtimeUrl);

            Assert.Equal(0, shutdownCount);
            Assert.Equal(0, startCount);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenManagedHostDeploymentIdentityMatches_ReusesWithoutRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v1"),
                Path.Combine(root, "payloads"),
                "1.0.0");
            var installed = store.Prepare();
            var deploymentIdentity = installed.DeploymentIdentity;
            store.Commit(installed);
            var runtimeUrl = new Uri("http://127.0.0.1:54321/");
            var shutdownCount = 0;
            var startCount = 0;
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(CreateHandshake()),
                isRuntimeHealthyAsync: (_, _) => Task.FromResult(true),
                shutdownRuntimeAsync: (_, _) =>
                {
                    shutdownCount++;
                    return Task.CompletedTask;
                },
                hostServiceManager: new StartProcessHostServiceManager(_ => startCount++),
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(
                    CreateHostHandshake("different-diagnostic-version", deploymentIdentity)),
                isHostInstanceLockAvailable: () => true,
                userHostPayloadStore: store);

            await manager.EnsureStartedAsync(runtimeUrl);
            await manager.EnsureStartedAsync(runtimeUrl);

            Assert.Equal(0, shutdownCount);
            Assert.Equal(0, startCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenManagedHostDeploymentIdentityDiffers_ReplacesExactlyOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payloadRoot = Path.Combine(root, "payloads");
            var oldStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v1"),
                payloadRoot,
                "1.0.0");
            var oldPayload = oldStore.Prepare();
            var oldIdentity = oldPayload.DeploymentIdentity;
            oldStore.Commit(oldPayload);
            var newStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v2"),
                payloadRoot,
                "1.0.0");
            var candidate = newStore.Prepare();
            var newIdentity = candidate.DeploymentIdentity;
            candidate.Dispose();
            var runtimeUrl = new Uri("http://127.0.0.1:54321/");
            var runtimeState = 0;
            var shutdownCount = 0;
            var startCount = 0;
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                    runtimeState is 0 or 2 ? CreateHandshake() : null),
                isRuntimeHealthyAsync: (_, _) => Task.FromResult(runtimeState is 0 or 2),
                shutdownRuntimeAsync: (_, _) =>
                {
                    shutdownCount++;
                    runtimeState = 1;
                    return Task.CompletedTask;
                },
                hostServiceManager: new StartProcessHostServiceManager(startInfo =>
                {
                    startCount++;
                    Assert.Contains("--deployment-identity", startInfo.ArgumentList);
                    Assert.Contains(newIdentity, startInfo.ArgumentList);
                    runtimeState = 2;
                }),
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(runtimeState switch
                {
                    0 => CreateHostHandshake("1.0.0", oldIdentity),
                    2 => CreateHostHandshake("1.0.0", newIdentity),
                    _ => null,
                }),
                userHostPayloadStore: newStore,
                isHostInstanceLockAvailable: () => true);

            await manager.EnsureStartedAsync(runtimeUrl);
            await manager.EnsureStartedAsync(runtimeUrl);

            Assert.Equal(1, shutdownCount);
            Assert.Equal(1, startCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenMatchingPayloadPathRequiresRepair_ReplacesBeforeCommit()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v1"),
                Path.Combine(root, "payloads"),
                "1.0.0");
            var installed = store.Prepare();
            var oldPath = installed.DirectoryPath;
            var identity = installed.DeploymentIdentity;
            store.Commit(installed);
            File.WriteAllText(
                Path.Combine(oldPath, OperatingSystem.IsWindows()
                    ? "Sunder.Host.Supervisor.exe"
                    : "Sunder.Host.Supervisor"),
                "corrupted-after-launch");
            var runtimeState = 0;
            var shutdownCount = 0;
            var launchCount = 0;
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                    runtimeState is 0 or 2 ? CreateHandshake() : null),
                isRuntimeHealthyAsync: (_, _) => Task.FromResult(runtimeState is 0 or 2),
                shutdownRuntimeAsync: (_, _) =>
                {
                    shutdownCount++;
                    runtimeState = 1;
                    return Task.CompletedTask;
                },
                hostServiceManager: new StartProcessHostServiceManager(startInfo =>
                {
                    launchCount++;
                    Assert.NotEqual(oldPath, Path.GetDirectoryName(startInfo.FileName));
                    runtimeState = 2;
                }),
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(
                    runtimeState is 0 or 2 ? CreateHostHandshake("1.0.0", identity) : null),
                userHostPayloadStore: store,
                isHostInstanceLockAvailable: () => true);

            await manager.EnsureStartedAsync(new Uri("http://127.0.0.1:54321/"));

            Assert.Equal(1, shutdownCount);
            Assert.Equal(1, launchCount);
            Assert.False(Directory.Exists(oldPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData((int)HostServiceState.Failed, true)]
    [InlineData((int)HostServiceState.Absent, false)]
    public async Task EnsureStartedAsync_WhenReplacementIsTerminal_RestoresOnlyAfterExactTermination(
        int observedState,
        bool expectRestore)
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payloadRoot = Path.Combine(root, "payloads");
            var firstStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v1"),
                payloadRoot,
                "1.0.0");
            var first = firstStore.Prepare();
            var firstIdentity = first.DeploymentIdentity;
            firstStore.Commit(first);
            var secondStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v2"),
                payloadRoot,
                "2.0.0");
            var second = secondStore.Prepare();
            second.Dispose();
            var runtimeState = 0;
            var launchState = 0;
            var serviceManager = new TestHostServiceManager(
                 new HostServiceLaunchReceipt("test-backend", "sunder-host", 4400),
                 (_, _) => Task.FromResult(new HostServiceObservation(
                     (HostServiceState)observedState,
                    4400,
                    19,
                    "process-exit")),
                (_, _) => runtimeState = serviceManagerLaunchState());
            int serviceManagerLaunchState() => ++launchState == 1 ? 2 : 3;
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                    runtimeState == 3 ? CreateHandshake() : null),
                isRuntimeHealthyAsync: (_, _) => Task.FromResult(runtimeState is 0 or 3),
                shutdownRuntimeAsync: (_, _) =>
                {
                    runtimeState = 1;
                    return Task.CompletedTask;
                },
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(runtimeState switch
                {
                    0 or 3 => CreateHostHandshake("1.0.0", firstIdentity),
                    _ => null,
                }),
                userHostPayloadStore: secondStore,
                isHostInstanceLockAvailable: () => true,
                hostServiceManager: serviceManager);

            var exception = await Assert.ThrowsAsync<HostServiceStartupException>(() =>
                manager.EnsureStartedAsync(new Uri("http://127.0.0.1:54321/")));

            Assert.Equal(HostServiceStartupFailure.TerminalState, exception.Failure);
            Assert.Equal(expectRestore ? 2 : 1, serviceManager.LaunchCount);
            Assert.Equal(
                expectRestore ? [second.ExecutablePath, first.ExecutablePath] : [second.ExecutablePath],
                serviceManager.StartInfos.Select(info => info.FileName));
            Assert.Equal(expectRestore ? 0 : 1, serviceManager.StopCount);
            Assert.True(Directory.Exists(first.DirectoryPath));
            Assert.Equal(!expectRestore, Directory.Exists(second.DirectoryPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenLauncherDisplacesServiceBeforeReceipt_RestoresPreviousPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payloadRoot = Path.Combine(root, "payloads");
            var firstStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v1"),
                payloadRoot,
                "1.0.0");
            var first = firstStore.Prepare();
            var firstIdentity = first.DeploymentIdentity;
            firstStore.Commit(first);
            var secondStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v2"),
                payloadRoot,
                "2.0.0");
            var second = secondStore.Prepare();
            second.Dispose();
            var runtimeState = 0;
            var launchCount = 0;
            var serviceManager = new TestHostServiceManager(
                new HostServiceLaunchReceipt("test-backend", "sunder-host", 4500),
                (_, _) => Task.FromResult(HostServiceObservation.Unknown(4500)),
                (_, _) =>
                {
                    launchCount++;
                    if (launchCount == 1)
                    {
                        runtimeState = 1;
                        throw new HostServiceReplacementException(
                            new IOException("replacement launch failed"));
                    }
                    runtimeState = 3;
                });
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                    runtimeState == 3 ? CreateHandshake() : null),
                isRuntimeHealthyAsync: (_, _) => Task.FromResult(runtimeState == 3),
                shutdownRuntimeAsync: (_, _) => Task.CompletedTask,
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(
                    runtimeState == 3
                        ? CreateHostHandshake("1.0.0", firstIdentity)
                        : null),
                userHostPayloadStore: secondStore,
                isHostInstanceLockAvailable: () => true,
                hostServiceManager: serviceManager);

            await Assert.ThrowsAsync<HostServiceReplacementException>(() =>
                manager.EnsureStartedAsync(new Uri("http://127.0.0.1:54321/")));

            Assert.Equal(2, serviceManager.LaunchCount);
            Assert.Equal(
                [second.ExecutablePath, first.ExecutablePath],
                serviceManager.StartInfos.Select(info => info.FileName));
            Assert.True(Directory.Exists(first.DirectoryPath));
            Assert.Contains(
                "1.0.0",
                File.ReadAllText(Path.Combine(payloadRoot, "current.json")),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenReplacementReportsWrongIdentity_StopsExactReceiptAndRestoresPreviousPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payloadRoot = Path.Combine(root, "payloads");
            var firstStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v1"),
                payloadRoot,
                "1.0.0");
            var first = firstStore.Prepare();
            var firstIdentity = first.DeploymentIdentity;
            firstStore.Commit(first);
            var secondStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v2"),
                payloadRoot,
                "2.0.0");
            var second = secondStore.Prepare();
            var secondIdentity = second.DeploymentIdentity;
            second.Dispose();
            var runtimeState = 0;
            var launchState = 0;
            var serviceManager = new TestHostServiceManager(
                new HostServiceLaunchReceipt("test-backend", "sunder-host", 4401, "candidate-generation"),
                (_, _) => Task.FromResult(new HostServiceObservation(HostServiceState.Running, 4401)),
                (_, _) => runtimeState = ++launchState == 1 ? 2 : 3,
                (_, _) =>
                {
                    runtimeState = 1;
                    return Task.FromResult(true);
                });
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                    runtimeState is 2 or 3 ? CreateHandshake() : null),
                isRuntimeHealthyAsync: (_, _) => Task.FromResult(runtimeState is 0 or 2 or 3),
                shutdownRuntimeAsync: (_, _) =>
                {
                    runtimeState = 1;
                    return Task.CompletedTask;
                },
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(runtimeState switch
                {
                    0 or 2 or 3 => CreateHostHandshake("1.0.0", firstIdentity),
                    _ => null,
                }),
                userHostPayloadStore: secondStore,
                isHostInstanceLockAvailable: () => true,
                hostServiceManager: serviceManager);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.EnsureStartedAsync(new Uri("http://127.0.0.1:54321/")));

            Assert.Contains(secondIdentity, exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, serviceManager.StopCount);
            Assert.Equal(2, serviceManager.LaunchCount);
            Assert.Equal([second.ExecutablePath, first.ExecutablePath], serviceManager.StartInfos.Select(info => info.FileName));
            Assert.True(Directory.Exists(first.DirectoryPath));
            Assert.False(Directory.Exists(second.DirectoryPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenDevelopmentRuntimeIsUnmanaged_ReusesIt()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var shutdownCount = 0;
        var startCount = 0;
        using var manager = new RuntimeHostProcessManager(
            new AppStartupOptions(),
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(CreateHandshake()),
            isRuntimeHealthyAsync: (_, _) => Task.FromResult(true),
            shutdownRuntimeAsync: (_, _) =>
            {
                shutdownCount++;
                return Task.CompletedTask;
            },
            hostServiceManager: new StartProcessHostServiceManager(_ => startCount++),
            delayAsync: (_, _) => Task.CompletedTask,
            connectionInfoPath: Path.Combine(rootPath, "connection-v1.json"),
            tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(null));

        try
        {
            await manager.EnsureStartedAsync(new Uri("http://127.0.0.1:54321/"));
            await manager.EnsureStartedAsync(new Uri("http://127.0.0.1:54321/"));

            Assert.Equal(0, shutdownCount);
            Assert.Equal(0, startCount);
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
            hostServiceManager: new StartProcessHostServiceManager(_ =>
            {
                Interlocked.Increment(ref startCount);
                runtimeStarted = true;
            }),
            delayAsync: (_, _) => Task.CompletedTask,
            connectionInfoPath: connectionInfoPath,
            isRuntimeLeaseAvailable: () => true);

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

    [Fact]
    public async Task EnsureStartedAsync_WhenAnotherPublishedRuntimeIsRunning_PreservesItAndRejectsLaunch()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var publishedUrl = new Uri("http://127.0.0.1:5275/");
        var requestedUrl = new Uri("http://127.0.0.1:5276/");
        var connectionInfoPath = Path.Combine(rootPath, "connection-v1.json");
        var published = new RuntimeConnectionInfo(publishedUrl, "published-token");
        RuntimeConnectionInfoStore.Save(published, connectionInfoPath);
        var connectionState = new RuntimeConnectionState(requestedUrl);
        var startCount = 0;
        var manager = new RuntimeHostProcessManager(
            new AppStartupOptions(),
            runtimeConnectionState: connectionState,
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeHandshakeAsync: (url, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                url == publishedUrl ? CreateHandshake() : null),
            isRuntimeHealthyAsync: (url, _) => Task.FromResult(url == publishedUrl),
            shutdownRuntimeAsync: static (_, _) => Task.CompletedTask,
            hostServiceManager: new StartProcessHostServiceManager(_ => startCount++),
            connectionInfoPath: connectionInfoPath,
            tryGetHostHandshakeAsync: static (_, _) => Task.FromResult<HostHandshakeResponse?>(null));

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureStartedAsync(requestedUrl));

            Assert.Contains("is still running", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, startCount);
            Assert.Equal(published.BearerToken, RuntimeConnectionInfoStore.Load(connectionInfoPath)?.BearerToken);
        }
        finally
        {
            manager.Dispose();
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenManagedHostIsPublishedAtAnotherUrl_RebindsIt()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var publishedUrl = new Uri("http://127.0.0.1:5275/");
        var requestedUrl = new Uri("http://127.0.0.1:5276/");
        var connectionInfoPath = Path.Combine(rootPath, "connection-v1.json");
        RuntimeConnectionInfoStore.Save(
            new RuntimeConnectionInfo(publishedUrl, "published-token"),
            connectionInfoPath);
        var publishedHostRunning = true;
        var requestedRuntimeStarted = false;
        var shutdownCount = 0;
        var startCount = 0;
        using var manager = new RuntimeHostProcessManager(
            new AppStartupOptions(),
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeHandshakeAsync: (url, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                url == publishedUrl && publishedHostRunning
                    || url == requestedUrl && requestedRuntimeStarted
                        ? CreateHandshake()
                        : null),
            isRuntimeHealthyAsync: (url, _) => Task.FromResult(
                url == publishedUrl && publishedHostRunning
                || url == requestedUrl && requestedRuntimeStarted),
            shutdownRuntimeAsync: (url, _) =>
            {
                Assert.Equal(publishedUrl, url);
                shutdownCount++;
                publishedHostRunning = false;
                return Task.CompletedTask;
            },
            hostServiceManager: new StartProcessHostServiceManager(_ =>
            {
                startCount++;
                requestedRuntimeStarted = true;
            }),
            delayAsync: (_, _) => Task.CompletedTask,
            connectionInfoPath: connectionInfoPath,
            isRuntimeLeaseAvailable: () => true,
            tryGetHostHandshakeAsync: (url, _) => Task.FromResult<HostHandshakeResponse?>(
                url == publishedUrl && publishedHostRunning
                    ? CreateHostHandshake("1.0.0")
                    : null));

        try
        {
            await manager.EnsureStartedAsync(requestedUrl);

            Assert.Equal(1, shutdownCount);
            Assert.Equal(1, startCount);
            Assert.False(publishedHostRunning);
            Assert.True(requestedRuntimeStarted);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenAlternateUrlRebindFailsBeforeLaunch_RestoresPreviousPayloadAtOriginalUrl()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payloadRoot = Path.Combine(root, "payloads");
            var firstStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v1"),
                payloadRoot,
                "1.0.0");
            var first = firstStore.Prepare();
            var firstIdentity = first.DeploymentIdentity;
            firstStore.Commit(first);
            var secondStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v2"),
                payloadRoot,
                "2.0.0");
            var second = secondStore.Prepare();
            var secondIdentity = second.DeploymentIdentity;
            second.Dispose();
            var publishedUrl = new Uri("http://127.0.0.1:5275/");
            var requestedUrl = new Uri("http://127.0.0.1:5276/");
            var connectionInfoPath = Path.Combine(root, "connection-v1.json");
            RuntimeConnectionInfoStore.Save(
                new RuntimeConnectionInfo(publishedUrl, "published-token"),
                connectionInfoPath);
            Uri? runningUrl = publishedUrl;
            var requestedProbeFailed = false;
            var launchedPaths = new List<string>();
            var launchedUrls = new List<string>();
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                tryGetRuntimeHandshakeAsync: (url, _) =>
                {
                    if (url == requestedUrl && !requestedProbeFailed)
                    {
                        requestedProbeFailed = true;
                        throw new InvalidOperationException("Rebind pre-launch probe failed.");
                    }
                    return Task.FromResult<RuntimeHandshakeResponse?>(
                        url == runningUrl ? CreateHandshake() : null);
                },
                isRuntimeHealthyAsync: (url, _) => Task.FromResult(url == runningUrl),
                shutdownRuntimeAsync: (url, _) =>
                {
                    Assert.Equal(publishedUrl, url);
                    runningUrl = null;
                    return Task.CompletedTask;
                },
                hostServiceManager: new StartProcessHostServiceManager(startInfo =>
                {
                    launchedPaths.Add(startInfo.FileName);
                    launchedUrls.Add(startInfo.ArgumentList[^1]);
                    runningUrl = publishedUrl;
                }),
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: connectionInfoPath,
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (url, _) => Task.FromResult<HostHandshakeResponse?>(
                    url == runningUrl ? CreateHostHandshake("1.0.0", firstIdentity) : null),
                userHostPayloadStore: secondStore,
                isHostInstanceLockAvailable: () => true);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureStartedAsync(requestedUrl));

            Assert.Equal("Rebind pre-launch probe failed.", exception.Message);
            Assert.Equal([first.ExecutablePath], launchedPaths);
            Assert.Equal([publishedUrl.ToString().TrimEnd('/')], launchedUrls);
            Assert.Equal(publishedUrl, runningUrl);
            Assert.True(Directory.Exists(first.DirectoryPath));
            Assert.Single(Directory.EnumerateDirectories(payloadRoot));
            var descriptor = File.ReadAllText(Path.Combine(payloadRoot, "current.json"));
            Assert.Contains("1.0.0", descriptor, StringComparison.Ordinal);
            Assert.DoesNotContain("2.0.0", descriptor, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenConnectionPublishesAtDeadline_PerformsFinalProbe()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var runtimeUrl = new Uri("http://localhost:54321/");
        var connectionInfoPath = Path.Combine(rootPath, "connection-v1.json");
        var connection = new RuntimeConnectionInfo(runtimeUrl, "late-token");
        var connectionState = new RuntimeConnectionState(runtimeUrl);
        var timeProvider = new ManualTimeProvider();
        var delayCount = 0;
        var startCount = 0;
        var manager = new RuntimeHostProcessManager(
            new AppStartupOptions(),
            runtimeConnectionState: connectionState,
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                connectionState.ConnectionInfo?.BearerToken == connection.BearerToken
                    ? CreateHandshake()
                    : null),
            isRuntimeHealthyAsync: (_, _) => Task.FromResult(false),
            hostServiceManager: new StartProcessHostServiceManager(_ => startCount++),
            delayAsync: (delay, _) =>
            {
                timeProvider.Advance(delay);
                if (Interlocked.Increment(ref delayCount) == 2)
                {
                    RuntimeConnectionInfoStore.Save(connection, connectionInfoPath);
                }
                return Task.CompletedTask;
            },
            connectionInfoPath: connectionInfoPath,
            timeProvider: timeProvider,
            startupTimeout: TimeSpan.FromMilliseconds(800),
            isRuntimeLeaseAvailable: () => true);

        try
        {
            await manager.EnsureStartedAsync(runtimeUrl);

            Assert.Equal(1, startCount);
            Assert.Equal(2, delayCount);
            Assert.Equal(connection.BearerToken, connectionState.ConnectionInfo?.BearerToken);
        }
        finally
        {
            manager.Dispose();
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenHostPublishesBeforeWorkerReady_PreservesAndReusesIt()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var runtimeUrl = new Uri("http://127.0.0.1:54321/");
        var connectionInfoPath = Path.Combine(rootPath, "connection-v1.json");
        var connection = new RuntimeConnectionInfo(runtimeUrl, "late-host-token");
        var hostAvailable = false;
        var runtimeReady = false;
        var ensureCount = 0;
        var startCount = 0;
        using var manager = new RuntimeHostProcessManager(
            new AppStartupOptions(),
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                runtimeReady ? CreateHandshake() : null),
            isRuntimeHealthyAsync: (_, _) => Task.FromResult(false),
            shutdownRuntimeAsync: (_, _) => Task.CompletedTask,
            hostServiceManager: new StartProcessHostServiceManager(_ => startCount++),
            delayAsync: (_, _) => Task.CompletedTask,
            connectionInfoPath: connectionInfoPath,
            isRuntimeLeaseAvailable: () => true,
            tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(
                hostAvailable ? CreateHostHandshake("1.0.0") : null),
            tryEnsureSupervisedRuntimeStartedAsync: (_, _) =>
            {
                ensureCount++;
                if (!hostAvailable)
                {
                    hostAvailable = true;
                    RuntimeConnectionInfoStore.Save(connection, connectionInfoPath);
                    return Task.FromResult(false);
                }
                runtimeReady = true;
                return Task.FromResult(true);
            });

        try
        {
            await manager.EnsureStartedAsync(runtimeUrl);

            Assert.Equal(0, startCount);
            Assert.Equal(2, ensureCount);
            Assert.Equal(connection.BearerToken, RuntimeConnectionInfoStore.Load(connectionInfoPath)?.BearerToken);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_AfterTimeoutReusesLateRuntimeWithoutSecondLaunch()
    {
        var (rootPath, runtimeHostPath) = await CreateRuntimeHostFileAsync();
        var runtimeUrl = new Uri("http://localhost:54321/");
        var connectionInfoPath = Path.Combine(rootPath, "connection-v1.json");
        var connection = new RuntimeConnectionInfo(runtimeUrl, "late-token");
        var connectionState = new RuntimeConnectionState(runtimeUrl);
        var timeProvider = new ManualTimeProvider();
        var startCount = 0;
        var manager = new RuntimeHostProcessManager(
            new AppStartupOptions(),
            runtimeConnectionState: connectionState,
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                connectionState.ConnectionInfo?.BearerToken == connection.BearerToken
                    ? CreateHandshake()
                    : null),
            isRuntimeHealthyAsync: (_, _) => Task.FromResult(false),
            hostServiceManager: new StartProcessHostServiceManager(_ => startCount++),
            delayAsync: (delay, _) =>
            {
                timeProvider.Advance(delay);
                return Task.CompletedTask;
            },
            connectionInfoPath: connectionInfoPath,
            timeProvider: timeProvider,
            startupTimeout: TimeSpan.FromMilliseconds(400),
            isRuntimeLeaseAvailable: () => true);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureStartedAsync(runtimeUrl));
            Assert.Contains("may still be starting", exception.Message, StringComparison.Ordinal);

            RuntimeConnectionInfoStore.Save(connection, connectionInfoPath);
            await manager.EnsureStartedAsync(runtimeUrl);

            Assert.Equal(1, startCount);
        }
        finally
        {
            manager.Dispose();
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenBundledHostVersionChanges_ActivatesNewUserPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payloadRoot = Path.Combine(root, "payloads");
            var firstStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v1"),
                payloadRoot,
                "1.0.0");
            var first = firstStore.Prepare();
            var firstIdentity = first.DeploymentIdentity;
            firstStore.Commit(first);
            var secondStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v2"),
                payloadRoot,
                "2.0.0");
            var second = secondStore.Prepare();
            var secondIdentity = second.DeploymentIdentity;
            second.Dispose();
            var runtimeUrl = new Uri("http://127.0.0.1:54321/");
            var running = true;
            var replacementStarted = false;
            string? launchedPath = null;
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                    replacementStarted ? CreateHandshake() : null),
                isRuntimeHealthyAsync: (_, _) => Task.FromResult(running || replacementStarted),
                shutdownRuntimeAsync: (_, _) =>
                {
                    running = false;
                    replacementStarted = false;
                    return Task.CompletedTask;
                },
                hostServiceManager: new StartProcessHostServiceManager(startInfo =>
                {
                    launchedPath = startInfo.FileName;
                    replacementStarted = true;
                }),
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(
                    replacementStarted
                        ? CreateHostHandshake("2.0.0", secondIdentity)
                        : CreateHostHandshake("1.0.0", firstIdentity)),
                userHostPayloadStore: secondStore);

            await manager.EnsureStartedAsync(runtimeUrl);

            Assert.NotNull(launchedPath);
            Assert.Contains("host-", launchedPath, StringComparison.Ordinal);
            Assert.False(Directory.Exists(first.DirectoryPath));
            Assert.Contains("2.0.0", File.ReadAllText(Path.Combine(payloadRoot, "current.json")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenLegacyDirectRuntimeIsCompatible_ReplacesItWithSupervisor()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payloadRoot = Path.Combine(root, "payloads");
            var store = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v1"),
                payloadRoot,
                "1.0.0");
            var candidate = store.Prepare();
            var candidateIdentity = candidate.DeploymentIdentity;
            candidate.Dispose();
            var runtimeUrl = new Uri("http://127.0.0.1:54321/");
            var runtimeState = 0;
            var shutdownCount = 0;
            var startCount = 0;
            string? launchedPath = null;
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                    runtimeState is 0 or 2 ? CreateHandshake() : null),
                isRuntimeHealthyAsync: (_, _) => Task.FromResult(runtimeState is 0 or 2),
                shutdownRuntimeAsync: (_, _) =>
                {
                    shutdownCount++;
                    runtimeState = 1;
                    return Task.CompletedTask;
                },
                hostServiceManager: new StartProcessHostServiceManager(startInfo =>
                {
                    startCount++;
                    launchedPath = startInfo.FileName;
                    runtimeState = 2;
                }),
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(
                    runtimeState == 2 ? CreateHostHandshake("1.0.0", candidateIdentity) : null),
                userHostPayloadStore: store);

            await manager.EnsureStartedAsync(runtimeUrl);

            Assert.Equal(1, shutdownCount);
            Assert.Equal(1, startCount);
            Assert.Contains("host-", launchedPath, StringComparison.Ordinal);
            Assert.Contains("1.0.0", File.ReadAllText(Path.Combine(payloadRoot, "current.json")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenFailedReplacementDoesNotStop_DoesNotLaunchRollbackPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payloadRoot = Path.Combine(root, "payloads");
            var firstStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v1"),
                payloadRoot,
                "1.0.0");
            var first = firstStore.Prepare();
            var firstIdentity = first.DeploymentIdentity;
            firstStore.Commit(first);
            var secondStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v2"),
                payloadRoot,
                "2.0.0");
            var second = secondStore.Prepare();
            var secondIdentity = second.DeploymentIdentity;
            second.Dispose();
            var runtimeUrl = new Uri("http://127.0.0.1:54321/");
            var runtimeState = 0;
            var shutdownCount = 0;
            var launchCount = 0;
            var timeProvider = new ManualTimeProvider();
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                    runtimeState == 0 ? CreateHandshake() : null),
                isRuntimeHealthyAsync: (_, _) => Task.FromResult(runtimeState is 0 or 2),
                shutdownRuntimeAsync: (_, _) =>
                {
                    shutdownCount++;
                    if (runtimeState == 0)
                    {
                        runtimeState = 1;
                    }
                    return Task.CompletedTask;
                },
                hostServiceManager: new StartProcessHostServiceManager(_ =>
                {
                    launchCount++;
                    runtimeState = 2;
                }),
                delayAsync: (delay, _) =>
                {
                    timeProvider.Advance(delay);
                    return Task.CompletedTask;
                },
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                timeProvider: timeProvider,
                startupTimeout: TimeSpan.FromMilliseconds(400),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(
                    CreateHostHandshake("1.0.0", firstIdentity)),
                userHostPayloadStore: secondStore);

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureStartedAsync(runtimeUrl));

            Assert.Equal(1, shutdownCount);
            Assert.Equal(1, launchCount);
            Assert.True(Directory.Exists(first.DirectoryPath));
            Assert.True(Directory.Exists(second.DirectoryPath));
            var descriptor = File.ReadAllText(Path.Combine(payloadRoot, "current.json"));
            Assert.Contains("1.0.0", descriptor, StringComparison.Ordinal);
            Assert.DoesNotContain("2.0.0", descriptor, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenReplacementValidationFails_RestoresPreviousPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payloadRoot = Path.Combine(root, "payloads");
            var firstStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v1"),
                payloadRoot,
                "1.0.0");
            var first = firstStore.Prepare();
            var firstIdentity = first.DeploymentIdentity;
            firstStore.Commit(first);
            var secondStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v2"),
                payloadRoot,
                "2.0.0");
            var second = secondStore.Prepare();
            var secondIdentity = second.DeploymentIdentity;
            second.Dispose();
            var runtimeUrl = new Uri("http://127.0.0.1:54321/");
            var runtimeState = 0;
            var shutdownCount = 0;
            var launchedPaths = new List<string>();
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                    runtimeState is 2 or 3 ? CreateHandshake() : null),
                isRuntimeHealthyAsync: (_, _) => Task.FromResult(runtimeState is 0 or 2 or 3),
                shutdownRuntimeAsync: (_, _) =>
                {
                    shutdownCount++;
                    runtimeState = 1;
                    return Task.CompletedTask;
                },
                hostServiceManager: new StartProcessHostServiceManager(startInfo =>
                {
                    launchedPaths.Add(startInfo.FileName);
                    runtimeState = launchedPaths.Count == 1 ? 2 : 3;
                }),
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(runtimeState switch
                {
                    0 => CreateHostHandshake("1.0.0", firstIdentity),
                    2 => CreateHostHandshake("2.0.0", firstIdentity),
                    3 => CreateHostHandshake("1.0.0", firstIdentity),
                    _ => null,
                }),
                userHostPayloadStore: secondStore,
                isHostInstanceLockAvailable: () => true);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureStartedAsync(runtimeUrl));

            Assert.Contains($"instead of staged payload identity '{secondIdentity}'", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, shutdownCount);
            Assert.Equal([second.ExecutablePath], launchedPaths);
            Assert.True(Directory.Exists(first.DirectoryPath));
            Assert.True(Directory.Exists(second.DirectoryPath));
            var descriptor = File.ReadAllText(Path.Combine(payloadRoot, "current.json"));
            Assert.Contains("1.0.0", descriptor, StringComparison.Ordinal);
            Assert.DoesNotContain("2.0.0", descriptor, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenPreLaunchReplacementProbeFails_RestoresPreviousPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payloadRoot = Path.Combine(root, "payloads");
            var firstStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v1"),
                payloadRoot,
                "1.0.0");
            var first = firstStore.Prepare();
            var firstIdentity = first.DeploymentIdentity;
            firstStore.Commit(first);
            var secondStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v2"),
                payloadRoot,
                "2.0.0");
            var runtimeUrl = new Uri("http://127.0.0.1:54321/");
            var runtimeState = 0;
            var runtimeProbeCount = 0;
            var launchedPaths = new List<string>();
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                tryGetRuntimeHandshakeAsync: (_, _) =>
                {
                    if (runtimeState == 1 && runtimeProbeCount++ == 0)
                    {
                        throw new InvalidOperationException("Replacement pre-launch probe failed.");
                    }
                    return Task.FromResult<RuntimeHandshakeResponse?>(
                        runtimeState == 2 ? CreateHandshake() : null);
                },
                isRuntimeHealthyAsync: (_, _) => Task.FromResult(runtimeState is 0 or 2),
                shutdownRuntimeAsync: (_, _) =>
                {
                    runtimeState = 1;
                    return Task.CompletedTask;
                },
                hostServiceManager: new StartProcessHostServiceManager(startInfo =>
                {
                    launchedPaths.Add(startInfo.FileName);
                    runtimeState = 2;
                }),
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(runtimeState switch
                {
                    0 or 2 => CreateHostHandshake("1.0.0", firstIdentity),
                    _ => null,
                }),
                userHostPayloadStore: secondStore,
                isHostInstanceLockAvailable: () => true);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureStartedAsync(runtimeUrl));

            Assert.Equal("Replacement pre-launch probe failed.", exception.Message);
            Assert.Equal([first.ExecutablePath], launchedPaths);
            Assert.True(Directory.Exists(first.DirectoryPath));
            Assert.Contains("1.0.0", File.ReadAllText(Path.Combine(payloadRoot, "current.json")), StringComparison.Ordinal);
            Assert.DoesNotContain("2.0.0", File.ReadAllText(Path.Combine(payloadRoot, "current.json")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenReplacementLaunchIsNotObservable_PreservesBothPayloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payloadRoot = Path.Combine(root, "payloads");
            var firstStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v1"),
                payloadRoot,
                "1.0.0");
            var first = firstStore.Prepare();
            var firstIdentity = first.DeploymentIdentity;
            firstStore.Commit(first);
            var secondStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v2"),
                payloadRoot,
                "2.0.0");
            var second = secondStore.Prepare();
            second.Dispose();
            var runtimeUrl = new Uri("http://127.0.0.1:54321/");
            var runtimeState = 0;
            var shutdownCount = 0;
            var launchCount = 0;
            var timeProvider = new ManualTimeProvider();
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(null),
                isRuntimeHealthyAsync: (_, _) => Task.FromResult(runtimeState is 0 or 2),
                shutdownRuntimeAsync: (_, _) =>
                {
                    shutdownCount++;
                    runtimeState = 1;
                    return Task.CompletedTask;
                },
                hostServiceManager: new StartProcessHostServiceManager(_ =>
                {
                    launchCount++;
                    runtimeState = 2;
                }),
                delayAsync: (delay, _) =>
                {
                    timeProvider.Advance(delay);
                    return Task.CompletedTask;
                },
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                timeProvider: timeProvider,
                startupTimeout: TimeSpan.FromMilliseconds(400),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(
                    runtimeState == 0 ? CreateHostHandshake("1.0.0", firstIdentity) : null),
                userHostPayloadStore: secondStore);

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureStartedAsync(runtimeUrl));

            Assert.Equal(1, shutdownCount);
            Assert.Equal(1, launchCount);
            Assert.True(Directory.Exists(first.DirectoryPath));
            Assert.True(Directory.Exists(second.DirectoryPath));
            var descriptor = File.ReadAllText(Path.Combine(payloadRoot, "current.json"));
            Assert.Contains("1.0.0", descriptor, StringComparison.Ordinal);
            Assert.DoesNotContain("2.0.0", descriptor, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenNewSupervisorPreservedStoppedIntent_StartsItsWorker()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var supervisorPath = Path.Combine(
                root,
                OperatingSystem.IsWindows() ? "Sunder.Host.Supervisor.exe" : "Sunder.Host.Supervisor");
            File.WriteAllText(supervisorPath, string.Empty);
            var runtimeUrl = new Uri("http://127.0.0.1:54321/");
            var supervisorStarted = false;
            var workerStarted = false;
            var ensureCount = 0;
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                resolveRuntimeHostPath: () => supervisorPath,
                tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                    workerStarted ? CreateHandshake() : null),
                isRuntimeHealthyAsync: (_, _) => Task.FromResult(supervisorStarted),
                shutdownRuntimeAsync: (_, _) => Task.CompletedTask,
                hostServiceManager: new StartProcessHostServiceManager(_ => supervisorStarted = true),
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(
                    supervisorStarted ? CreateHostHandshake("1.0.0") : null),
                tryEnsureSupervisedRuntimeStartedAsync: (_, _) =>
                {
                    ensureCount++;
                    if (!supervisorStarted)
                    {
                        return Task.FromResult(false);
                    }
                    workerStarted = true;
                    return Task.FromResult(true);
                });

            await manager.EnsureStartedAsync(runtimeUrl);

            Assert.True(supervisorStarted);
            Assert.True(workerStarted);
            Assert.Equal(2, ensureCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenLaunchedPayloadHostProtocolIsIncompatible_DoesNotCommitPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payloadRoot = Path.Combine(root, "payloads");
            var store = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v1"),
                payloadRoot,
                "1.0.0");
            var candidate = store.Prepare();
            var candidateIdentity = candidate.DeploymentIdentity;
            candidate.Dispose();
            var runtimeUrl = new Uri("http://127.0.0.1:54321/");
            var started = false;
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(
                    started ? CreateHandshake() : null),
                isRuntimeHealthyAsync: (_, _) => Task.FromResult(started),
                shutdownRuntimeAsync: (_, _) =>
                {
                    started = false;
                    return Task.CompletedTask;
                },
                hostServiceManager: new StartProcessHostServiceManager(_ => started = true),
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(
                    started
                        ? CreateHostHandshake("1.0.0", candidateIdentity) with
                        {
                            SupportedFeatures = [HostProtocolFeatures.RuntimeGatewayV1],
                        }
                        : null),
                userHostPayloadStore: store);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureStartedAsync(runtimeUrl));

            Assert.Contains(HostProtocolFeatures.RuntimeLifecycleV1, exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(payloadRoot, "current.json")));
            Assert.Empty(Directory.EnumerateDirectories(payloadRoot));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenPersistentLaunchOutcomeIsUnknown_PreservesPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payloadRoot = Path.Combine(root, "payloads");
            var store = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v1"),
                payloadRoot,
                "1.0.0");
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(null),
                isRuntimeHealthyAsync: (_, _) => Task.FromResult(false),
                shutdownRuntimeAsync: (_, _) => Task.CompletedTask,
                hostServiceManager: new StartProcessHostServiceManager(
                    _ => throw new IOException("Persistent launcher outcome is unknown.")),
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(null),
                userHostPayloadStore: store);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureStartedAsync(new Uri("http://127.0.0.1:54321/")));

            Assert.Single(Directory.EnumerateDirectories(payloadRoot));
            Assert.False(File.Exists(Path.Combine(payloadRoot, "current.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenAcceptedLaunchIsNotYetObservable_PreservesPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payloadRoot = Path.Combine(root, "payloads");
            var store = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v1"),
                payloadRoot,
                "1.0.0");
            var timeProvider = new ManualTimeProvider();
            using var manager = new RuntimeHostProcessManager(
                new AppStartupOptions(),
                tryGetRuntimeHandshakeAsync: (_, _) => Task.FromResult<RuntimeHandshakeResponse?>(null),
                isRuntimeHealthyAsync: (_, _) => Task.FromResult(false),
                shutdownRuntimeAsync: (_, _) => Task.CompletedTask,
                hostServiceManager: new StartProcessHostServiceManager(_ => { }),
                delayAsync: (delay, _) =>
                {
                    timeProvider.Advance(delay);
                    return Task.CompletedTask;
                },
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                timeProvider: timeProvider,
                startupTimeout: TimeSpan.FromMilliseconds(400),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(null),
                userHostPayloadStore: store);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureStartedAsync(new Uri("http://127.0.0.1:54321/")));

            Assert.Single(Directory.EnumerateDirectories(payloadRoot));
            Assert.False(File.Exists(Path.Combine(payloadRoot, "current.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveRuntimeHostPath_PrefersConfiguredThenBundledThenCoLocatedPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var configured = Path.Combine(root, "configured");
        var appBase = Path.Combine(root, "app");
        var bundled = Path.Combine(appBase, "RuntimeHost");
        Directory.CreateDirectory(configured);
        Directory.CreateDirectory(bundled);
        var fileName = OperatingSystem.IsWindows() ? "Sunder.Host.Supervisor.exe" : "Sunder.Host.Supervisor";
        var configuredFile = Path.Combine(configured, fileName);
        var bundledFile = Path.Combine(bundled, fileName);
        var colocatedFile = Path.Combine(appBase, fileName);
        File.WriteAllText(configuredFile, string.Empty);
        File.WriteAllText(bundledFile, string.Empty);
        File.WriteAllText(colocatedFile, string.Empty);
        try
        {
            Assert.Equal(configuredFile, RuntimeHostProcessManager.ResolveRuntimeHostPath(configured, appBase));
            File.Delete(configuredFile);
            Assert.Equal(bundledFile, RuntimeHostProcessManager.ResolveRuntimeHostPath(configured, appBase));
            File.Delete(bundledFile);
            Assert.Equal(colocatedFile, RuntimeHostProcessManager.ResolveRuntimeHostPath(configured, appBase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveRuntimeHostPath_DoesNotProbeSiblingDefaultDebugOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var appBase = Path.Combine(root, "Sunder.App", "alternate-bin", "Debug", "net10.0");
        var staleSupervisor = Path.Combine(
            root,
            "Sunder.Host.Supervisor",
            "bin",
            "Debug",
            "net10.0");
        Directory.CreateDirectory(appBase);
        Directory.CreateDirectory(staleSupervisor);
        File.WriteAllText(
            Path.Combine(staleSupervisor, OperatingSystem.IsWindows() ? "Sunder.Host.Supervisor.exe" : "Sunder.Host.Supervisor"),
            string.Empty);
        try
        {
            Assert.Null(RuntimeHostProcessManager.ResolveRuntimeHostPath(null, appBase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RuntimeHostProcessManager CreateObservedLaunchManager(
        string runtimeHostPath,
        string connectionInfoPath,
        IHostServiceManager hostServiceManager,
        Func<Uri, CancellationToken, Task<RuntimeHandshakeResponse?>> tryGetRuntimeHandshakeAsync,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeProvider? timeProvider = null,
        TimeSpan? startupTimeout = null)
        => new(
            new AppStartupOptions(),
            resolveRuntimeHostPath: () => runtimeHostPath,
            tryGetRuntimeHandshakeAsync: tryGetRuntimeHandshakeAsync,
            isRuntimeHealthyAsync: static (_, _) => Task.FromResult(false),
            shutdownRuntimeAsync: static (_, _) => Task.CompletedTask,
            delayAsync: delayAsync ?? (static (_, _) => Task.CompletedTask),
            connectionInfoPath: connectionInfoPath,
            timeProvider: timeProvider,
            startupTimeout: startupTimeout ?? TimeSpan.FromSeconds(2),
            isRuntimeLeaseAvailable: static () => true,
            hostServiceManager: hostServiceManager);

    private static HostHandshakeResponse CreateHostHandshake(
        string version,
        string? deploymentIdentity = null)
        => new HostHandshakeResponse(
            HostProtocol.Identity,
            HostProtocol.CurrentRevision,
            HostProtocol.MinimumSupportedRevision,
            HostProtocol.MaximumSupportedRevision,
            Guid.NewGuid(),
            Guid.NewGuid(),
            [
                HostProtocolFeatures.RuntimeGatewayV1,
                HostProtocolFeatures.RuntimeLifecycleV1,
                HostProtocolFeatures.DurableOperationsV1,
            ],
            new HostProductVersionDiagnostics("Sunder.Host.Supervisor", version, version))
        {
            DeploymentIdentity = deploymentIdentity,
        };

    private static string CreateSupervisorPayload(string root, string name)
    {
        var source = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(source, "RuntimeHost"));
        File.WriteAllText(
            Path.Combine(source, OperatingSystem.IsWindows() ? "Sunder.Host.Supervisor.exe" : "Sunder.Host.Supervisor"),
            name);
        File.WriteAllText(Path.Combine(source, "RuntimeHost", "worker"), name);
        return source;
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
            [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.AtomicPackageSnapshotV1],
            new RuntimeProductVersionDiagnostics("Other.Runtime", "not-a-protocol-version", "diagnostic-build"));

    private static async Task<(string RootPath, string RuntimeHostPath)> CreateRuntimeHostFileAsync()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var runtimeHostPath = Path.Combine(rootPath, OperatingSystem.IsWindows() ? "Sunder.Runtime.Host.exe" : "Sunder.Runtime.Host");
        await File.WriteAllTextAsync(runtimeHostPath, string.Empty);
        return (rootPath, runtimeHostPath);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
            _timestamp += duration.Ticks;
        }
    }

    private sealed class StartProcessHostServiceManager(Action<ProcessStartInfo> startProcess)
        : IHostServiceManager
    {
        public Task<HostServiceLaunchReceipt> ReconcileAndLaunchAsync(
            ProcessStartInfo startInfo,
            bool replaceExisting,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            startProcess(startInfo);
            return Task.FromResult(new HostServiceLaunchReceipt("test-injected", "sunder-host"));
        }

        public Task<HostServiceObservation> ObserveAsync(
            HostServiceLaunchReceipt receipt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(HostServiceObservation.Unknown(receipt.ProcessId));
        }

        public Task<bool> TryStopAsync(
            HostServiceLaunchReceipt receipt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }
    }

    private sealed class TestHostServiceManager(
        HostServiceLaunchReceipt receipt,
        Func<HostServiceLaunchReceipt, CancellationToken, Task<HostServiceObservation>> observeAsync,
        Action<ProcessStartInfo, bool>? launch = null,
        Func<HostServiceLaunchReceipt, CancellationToken, Task<bool>>? stopAsync = null)
        : IHostServiceManager
    {
        public int LaunchCount { get; private set; }

        public int ObserveCount { get; private set; }

        public int StopCount { get; private set; }

        public ProcessStartInfo? StartInfo { get; private set; }

        public List<ProcessStartInfo> StartInfos { get; } = [];

        public HostServiceLaunchReceipt? ObservedReceipt { get; private set; }

        public Task<HostServiceLaunchReceipt> ReconcileAndLaunchAsync(
            ProcessStartInfo startInfo,
            bool replaceExisting,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LaunchCount++;
            StartInfo = startInfo;
            StartInfos.Add(startInfo);
            launch?.Invoke(startInfo, replaceExisting);
            return Task.FromResult(receipt);
        }

        public Task<HostServiceObservation> ObserveAsync(
            HostServiceLaunchReceipt launchReceipt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveCount++;
            ObservedReceipt = launchReceipt;
            return observeAsync(launchReceipt, cancellationToken);
        }

        public Task<bool> TryStopAsync(
            HostServiceLaunchReceipt launchReceipt,
            CancellationToken cancellationToken)
        {
            StopCount++;
            return stopAsync?.Invoke(launchReceipt, cancellationToken) ?? Task.FromResult(false);
        }
    }

}
