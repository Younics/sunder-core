using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
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
    [InlineData("dev.sunder.runtime", 3, 3, 3, true)]
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
            startProcess: _ =>
            {
                Assert.True(leaseAvailable);
                replacementStarted = true;
            },
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
            startProcess: _ =>
            {
                Assert.True(leaseAvailable);
                replacementStarted = true;
            },
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
            startProcess: _ => startCount++,
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
            startProcess: _ => startCount++,
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
            startProcess: startInfo =>
            {
                Assert.Null(RuntimeConnectionInfoStore.Load(connectionInfoPath));
                capturedStartInfo = startInfo;
                runtimeStarted = true;
            },
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
            startProcess: _ =>
            {
                startCount++;
                runtimeRunning = true;
            },
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
            startProcess: _ => startCount++,
            connectionInfoPath: connectionInfoPath);

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
            startProcess: _ =>
            {
                startCount++;
                requestedRuntimeStarted = true;
            },
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
            firstStore.Commit(first);
            var secondStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v2"),
                payloadRoot,
                "2.0.0");
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
                startProcess: startInfo =>
                {
                    launchedPaths.Add(startInfo.FileName);
                    launchedUrls.Add(startInfo.ArgumentList[^1]);
                    runningUrl = publishedUrl;
                },
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: connectionInfoPath,
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (url, _) => Task.FromResult<HostHandshakeResponse?>(
                    url == runningUrl ? CreateHostHandshake("1.0.0") : null),
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
            startProcess: _ => startCount++,
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
            startProcess: _ => startCount++,
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
    public async Task RuntimeHealthProbe_WithStaleTokenStillDetectsOccupiedTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = Assert.IsType<IPEndPoint>(listener.LocalEndpoint);
            var runtimeUrl = new Uri($"http://127.0.0.1:{endpoint.Port}/");
            var connectionState = new RuntimeConnectionState(runtimeUrl);
            connectionState.SetConnection(new RuntimeConnectionInfo(runtimeUrl, "stale-token"));
            using var probe = new RuntimeHealthProbe(connectionState);

            Assert.True(await probe.IsRuntimeHealthyAsync(runtimeUrl, CancellationToken.None));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RuntimeHealthProbe_ShutdownRuntime_DoesNotRequireLifecycleFeatureNegotiation()
    {
        var runtimeUrl = new Uri("http://127.0.0.1:54321/");
        var connectionState = new RuntimeConnectionState(runtimeUrl);
        connectionState.SetConnection(new RuntimeConnectionInfo(runtimeUrl, "host-token"));
        var requests = new List<(HttpMethod Method, string Path)>();
        var handler = new RecordingHttpMessageHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new HostHandshakeResponse(
                    HostProtocol.Identity,
                    HostProtocol.CurrentRevision,
                    HostProtocol.MinimumSupportedRevision,
                    HostProtocol.MaximumSupportedRevision,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    [HostProtocolFeatures.RuntimeGatewayV1],
                    new HostProductVersionDiagnostics("Sunder.Host.Supervisor", "1.0.0", "test"))),
            };
        });
        using var probe = new RuntimeHealthProbe(connectionState, hostHandler: handler);

        await probe.ShutdownRuntimeAsync(runtimeUrl, CancellationToken.None);

        Assert.Equal(
            [
                (HttpMethod.Get, "/api/host/handshake"),
                (HttpMethod.Post, "/api/host/v1/shutdown"),
            ],
            requests);
    }

    [Fact]
    public async Task RuntimeHealthProbe_StartsAndPollsDurableHostOperation()
    {
        var runtimeUrl = new Uri("http://127.0.0.1:54321/");
        var connectionState = new RuntimeConnectionState(runtimeUrl);
        connectionState.SetConnection(new RuntimeConnectionInfo(runtimeUrl, "host-token"));
        var operationId = "11111111111111111111111111111111";
        var mutationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var now = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var accepted = new HostOperationDescriptor(
            operationId,
            mutationId,
            HostOperationKinds.RuntimeStart,
            4,
            HostOperationState.Accepted,
            now,
            now,
            null,
            null);
        var stopped = new HostRuntimeStatus(
            HostRuntimeDesiredState.Stopped,
            HostRuntimeState.Stopped,
            4,
            "1.0.0",
            null,
            null,
            null,
            null,
            null,
            null);
        var submittedOperation = accepted;
        var requests = new List<(HttpMethod Method, string Path)>();
        var handler = new RecordingHttpMessageHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            if (request.RequestUri.AbsolutePath == "/api/host/handshake")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new HostHandshakeResponse(
                        HostProtocol.Identity,
                        HostProtocol.CurrentRevision,
                        HostProtocol.MinimumSupportedRevision,
                        HostProtocol.MaximumSupportedRevision,
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        [HostProtocolFeatures.RuntimeLifecycleV1, HostProtocolFeatures.DurableOperationsV1],
                        new HostProductVersionDiagnostics("Sunder.Host.Supervisor", "1.0.0", "test"))),
                };
            }
            if (request.RequestUri.AbsolutePath == "/api/host/v1/status")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(stopped) };
            }
            if (request.Method == HttpMethod.Post)
            {
                var lifecycleRequest = request.Content!.ReadFromJsonAsync<HostLifecycleRequest>()
                    .GetAwaiter()
                    .GetResult()!;
                submittedOperation = accepted with
                {
                    MutationId = lifecycleRequest.MutationId,
                    ExpectedDeploymentGeneration = lifecycleRequest.ExpectedDeploymentGeneration,
                };
                var response = new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = JsonContent.Create(new HostLifecycleSubmission(submittedOperation, stopped)),
                };
                response.Headers.Location = new Uri($"/api/host/v1/operations/{operationId}", UriKind.Relative);
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(submittedOperation with
                {
                    State = HostOperationState.Succeeded,
                    UpdatedAtUtc = now.AddSeconds(1),
                    Message = "Runtime worker is ready.",
                }),
            };
        });
        using var probe = new RuntimeHealthProbe(connectionState, hostHandler: handler);

        Assert.True(await probe.TryEnsureSupervisedRuntimeStartedAsync(runtimeUrl, CancellationToken.None));
        Assert.Equal(
            [
                (HttpMethod.Get, "/api/host/handshake"),
                (HttpMethod.Get, "/api/host/v1/status"),
                (HttpMethod.Post, "/api/host/v1/runtime/start"),
                (HttpMethod.Get, $"/api/host/v1/operations/{operationId}"),
            ],
            requests);
    }

    [Fact]
    public async Task RuntimeHealthProbe_AppliesRequestedIntentAfterPriorOperationFails()
    {
        var runtimeUrl = new Uri("http://127.0.0.1:54321/");
        var connectionState = new RuntimeConnectionState(runtimeUrl);
        connectionState.SetConnection(new RuntimeConnectionInfo(runtimeUrl, "host-token"));
        var priorOperationId = "11111111111111111111111111111111";
        var requestedOperationId = "22222222222222222222222222222222";
        var now = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var priorOperation = new HostOperationDescriptor(
            priorOperationId,
            Guid.NewGuid(),
            HostOperationKinds.RuntimeStop,
            4,
            HostOperationState.Running,
            now,
            now,
            null,
            null);
        var activeStatus = new HostRuntimeStatus(
            HostRuntimeDesiredState.Stopped,
            HostRuntimeState.Stopping,
            5,
            "1.0.0",
            null,
            null,
            null,
            null,
            null,
            priorOperationId);
        var failedStatus = activeStatus with
        {
            State = HostRuntimeState.Failed,
            FailureCode = "host.worker-stop-failed",
            FailureMessage = "stop failed",
            ActiveOperationId = null,
        };
        var statusRequestCount = 0;
        var requestedMutationId = Guid.Empty;
        var requests = new List<(HttpMethod Method, string Path)>();
        var handler = new RecordingHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add((request.Method, path));
            if (path == "/api/host/handshake")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new HostHandshakeResponse(
                        HostProtocol.Identity,
                        HostProtocol.CurrentRevision,
                        HostProtocol.MinimumSupportedRevision,
                        HostProtocol.MaximumSupportedRevision,
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        [HostProtocolFeatures.RuntimeLifecycleV1, HostProtocolFeatures.DurableOperationsV1],
                        new HostProductVersionDiagnostics("Sunder.Host.Supervisor", "1.0.0", "test"))),
                };
            }
            if (path == "/api/host/v1/status")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        Interlocked.Increment(ref statusRequestCount) == 1 ? activeStatus : failedStatus),
                };
            }
            if (path.EndsWith(priorOperationId, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(priorOperation with
                    {
                        State = HostOperationState.Failed,
                        UpdatedAtUtc = now.AddSeconds(1),
                        Message = "stop failed",
                        FailureCode = "host.worker-stop-failed",
                    }),
                };
            }
            if (request.Method == HttpMethod.Post)
            {
                var lifecycleRequest = request.Content!.ReadFromJsonAsync<HostLifecycleRequest>()
                    .GetAwaiter()
                    .GetResult()!;
                requestedMutationId = lifecycleRequest.MutationId;
                var requestedOperation = new HostOperationDescriptor(
                    requestedOperationId,
                    lifecycleRequest.MutationId,
                    HostOperationKinds.RuntimeStart,
                    lifecycleRequest.ExpectedDeploymentGeneration,
                    HostOperationState.Accepted,
                    now,
                    now,
                    null,
                    null);
                var response = new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = JsonContent.Create(new HostLifecycleSubmission(requestedOperation, failedStatus)),
                };
                response.Headers.Location = new Uri(
                    $"/api/host/v1/operations/{requestedOperationId}",
                    UriKind.Relative);
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new HostOperationDescriptor(
                    requestedOperationId,
                    requestedMutationId,
                    HostOperationKinds.RuntimeStart,
                    5,
                    HostOperationState.Succeeded,
                    now,
                    now.AddSeconds(1),
                    "Runtime worker is ready.",
                    null)),
            };
        });
        using var probe = new RuntimeHealthProbe(connectionState, hostHandler: handler);

        Assert.True(await probe.TryEnsureSupervisedRuntimeStartedAsync(runtimeUrl, CancellationToken.None));
        Assert.Contains((HttpMethod.Post, "/api/host/v1/runtime/start"), requests);
        Assert.Contains((HttpMethod.Get, $"/api/host/v1/operations/{requestedOperationId}"), requests);
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
            firstStore.Commit(first);
            var secondStore = new UserHostPayloadStore(
                CreateSupervisorPayload(root, "source-v2"),
                payloadRoot,
                "2.0.0");
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
                startProcess: startInfo =>
                {
                    launchedPath = startInfo.FileName;
                    replacementStarted = true;
                },
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(
                    CreateHostHandshake(replacementStarted ? "2.0.0" : "1.0.0")),
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
                startProcess: startInfo =>
                {
                    startCount++;
                    launchedPath = startInfo.FileName;
                    runtimeState = 2;
                },
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(
                    runtimeState == 2 ? CreateHostHandshake("1.0.0") : null),
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
                startProcess: _ =>
                {
                    launchCount++;
                    runtimeState = 2;
                },
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
                    CreateHostHandshake("1.0.0")),
                userHostPayloadStore: secondStore);

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureStartedAsync(runtimeUrl));

            Assert.Equal(2, shutdownCount);
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
                startProcess: startInfo =>
                {
                    launchedPaths.Add(startInfo.FileName);
                    runtimeState = launchedPaths.Count == 1 ? 2 : 3;
                },
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(runtimeState switch
                {
                    0 => CreateHostHandshake("1.0.0"),
                    2 => CreateHostHandshake("1.0.0"),
                    3 => CreateHostHandshake("1.0.0"),
                    _ => null,
                }),
                userHostPayloadStore: secondStore,
                isHostInstanceLockAvailable: () => true);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureStartedAsync(runtimeUrl));

            Assert.Contains("instead of staged payload version '2.0.0'", exception.Message, StringComparison.Ordinal);
            Assert.Equal(2, shutdownCount);
            Assert.Equal([second.ExecutablePath, first.ExecutablePath], launchedPaths);
            Assert.True(Directory.Exists(first.DirectoryPath));
            Assert.False(Directory.Exists(second.DirectoryPath));
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
                startProcess: startInfo =>
                {
                    launchedPaths.Add(startInfo.FileName);
                    runtimeState = 2;
                },
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(runtimeState switch
                {
                    0 or 2 => CreateHostHandshake("1.0.0"),
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
                startProcess: _ =>
                {
                    launchCount++;
                    runtimeState = 2;
                },
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
                    runtimeState == 0 ? CreateHostHandshake("1.0.0") : null),
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
                startProcess: _ => supervisorStarted = true,
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
                startProcess: _ => started = true,
                delayAsync: (_, _) => Task.CompletedTask,
                connectionInfoPath: Path.Combine(root, "host-connection.json"),
                isRuntimeLeaseAvailable: () => true,
                tryGetHostHandshakeAsync: (_, _) => Task.FromResult<HostHandshakeResponse?>(
                    started
                        ? CreateHostHandshake("1.0.0") with
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
                startProcess: _ => throw new IOException("Persistent launcher outcome is unknown."),
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
                startProcess: _ => { },
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

    private static HostHandshakeResponse CreateHostHandshake(string version)
        => new(
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
            new HostProductVersionDiagnostics("Sunder.Host.Supervisor", version, version));

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

    private sealed class RecordingHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
