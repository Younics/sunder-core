using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ProcessRuntimeWorkerTests
{
    [Fact]
    public async Task Worker_HandshakeInvokeStreamCancellationAndShutdown()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        await fixture.StartAndActivateAsync();

        var result = await fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" }),
            CancellationToken.None);
        Assert.True(result.GetProperty("accepted").GetBoolean());

        var events = new List<JsonElement>();
        await foreach (var item in fixture.Worker.InvokeServerStreamAsync(
                           "example.provider",
                           fixture.InvocationContext(),
                           "messages",
                           "watch",
                           JsonSerializer.SerializeToElement(new { message = "hello" }),
                           CancellationToken.None))
        {
            events.Add(item);
        }
        Assert.Equal([1, 2], events.Select(item => item.GetProperty("sequence").GetInt32()));

        using var cancellation = new CancellationTokenSource();
        var call = fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "wait",
            JsonSerializer.SerializeToElement(new { message = "wait" }),
            cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);

        await Task.WhenAll(fixture.Worker.StopAsync(), fixture.Worker.StopAsync());
        await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Worker_PreActivationProviderCallIsUnavailableWithoutFaultingGeneration()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        await fixture.Worker.StartAsync();
        await fixture.Worker.CommitGenerationAsync(
            new Sunder.Sdk.Abstractions.PackageRuntimeGeneration(fixture.ActivationId, 1));

        var unavailable = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await fixture.Worker.InvokeUnaryAsync(
                "example.provider",
                fixture.InvocationContext(),
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "early" }),
                CancellationToken.None));

        Assert.Equal(SunderRpcErrorKind.Unavailable, unavailable.Error.Kind);
        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);

        fixture.Worker.ActivateGeneration();
        var result = await fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "ready" }),
            CancellationToken.None);
        Assert.True(result.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task Worker_StopCompletesWhenForceStopCallbackThrows()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        await fixture.StartAndActivateAsync();
        var forceStop = Assert.IsType<CancellationTokenSource>(typeof(ProcessRuntimeWorker)
            .GetField("_forceStop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(fixture.Worker));
        using var callback = forceStop.Token.Register(
            static () => throw new InvalidOperationException("force-stop callback failed"));

        await fixture.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Worker_ContentRegisterOpenAndDiscardRemainBoundToHostInvocation()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        var activation = await fixture.PublishBrokerSessionAsync();
        var input = await fixture.RegisterCallerContentAsync("caller-payload");

        var result = await activation.Client.InvokeAsync(
            activation.Endpoint,
            "messages",
            "content-roundtrip",
            JsonSerializer.SerializeToElement(new
            {
                message = "content",
                content = WorkerWire.ToWire(input),
            }));

        Assert.True(result.GetProperty("accepted").GetBoolean());
        Assert.True(result.GetProperty("openedFileDiscarded").GetBoolean());
        var output = ReadContentReference(result.GetProperty("content"));
        var lease = Assert.IsType<RuntimeRpcContentLease>(fixture.Transfers.AcquireRpcContent(
            output,
            "process.test",
            "caller.package",
            fixture.Owner.Generation));
        try
        {
            Assert.Equal("caller-payload-processed", await File.ReadAllTextAsync(lease.FilePath));
        }
        finally
        {
            fixture.Transfers.ReleaseRpcContent(lease);
        }
    }

    [Fact]
    public async Task Worker_UsesSanitizedExplicitEnvironmentWithoutRuntimeTokens()
    {
        await using var fixture = await WorkerFixture.CreateAsync("environment");
        await fixture.StartAndActivateAsync();

        var result = await fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "environment",
            JsonSerializer.SerializeToElement(new { message = "environment" }),
            CancellationToken.None);

        Assert.Equal(JsonValueKind.Null, result.GetProperty("nodeOptions").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("runtimeToken").ValueKind);
        Assert.Equal("process.test", result.GetProperty("packageId").GetString());
        Assert.Equal("1.0.0", result.GetProperty("packageVersion").GetString());
        Assert.Equal(fixture.ActivationId.ToString("N"), result.GetProperty("activationId").GetString());
        Assert.Equal(fixture.Prepared.SessionId, result.GetProperty("sessionId").GetString());
        Assert.Equal(
            new DirectoryInfo(fixture.Prepared.ShadowFolder).Name,
            new DirectoryInfo(result.GetProperty("workingPath").GetString()!).Name);
        Assert.True(Path.IsPathFullyQualified(result.GetProperty("dataPath").GetString()!));
        Assert.EndsWith(Path.Combine("data", "state.json"), result.GetProperty("statePath").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("Z", result.GetProperty("deadlineUtc").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("malformed-frame")]
    [InlineData("crash")]
    [InlineData("caller-spoof")]
    [InlineData("content-spoof")]
    public async Task Worker_ProtocolViolationCrashAndCallerSpoofFaultExactGeneration(string mode)
    {
        await using var fixture = await WorkerFixture.CreateAsync(mode);
        await fixture.StartAndActivateAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Worker_RejectsDuplicateTerminalFrames()
    {
        await using var fixture = await WorkerFixture.CreateAsync("duplicate-terminal");
        await fixture.StartAndActivateAsync();

        var result = await fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" }),
            CancellationToken.None);
        Assert.True(result.GetProperty("accepted").GetBoolean());
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Worker_BoundsStartupShutdownAndStderr()
    {
        await using (var startup = await WorkerFixture.CreateAsync("startup-hang", new RuntimeProcessPolicyOptions
                     {
                         StartupTimeout = TimeSpan.FromMilliseconds(150),
                     }))
        {
            await Assert.ThrowsAsync<TimeoutException>(() => startup.Worker.StartAsync());
        }

        await using (var shutdown = await WorkerFixture.CreateAsync("shutdown-hang", new RuntimeProcessPolicyOptions
                     {
                         ShutdownTimeout = TimeSpan.FromMilliseconds(150),
                     }))
        {
            await shutdown.StartAndActivateAsync();
            var started = Stopwatch.GetTimestamp();
            await shutdown.Worker.StopAsync();
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        }

        await using (var activation = await WorkerFixture.CreateAsync("activation-hang", new RuntimeProcessPolicyOptions
                     {
                         ActivationTimeout = TimeSpan.FromMilliseconds(150),
                     }))
        {
            await activation.StartAndActivateAsync();
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await activation.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        var logger = new CapturingLogger();
        await using (var stderr = await WorkerFixture.CreateAsync("stderr", new RuntimeProcessPolicyOptions
                     {
                         MaxStderrBytes = 1024,
                         MaxStderrLineCharacters = 128,
                     }, logger))
        {
            await stderr.StartAndActivateAsync();
            await stderr.Worker.StopAsync();
        }
        Assert.Contains(logger.Messages, message => message.Contains("exceeded 1024 bytes", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains(new string('x', 256), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("invalid-output")]
    [InlineData("handler-fault")]
    public async Task Broker_InvalidFakeWorkerOutputFaultsExactProcessActivation(string mode)
    {
        await using var fixture = await WorkerFixture.CreateAsync(mode);
        var activation = await fixture.PublishBrokerSessionAsync();

        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await activation.Client.InvokeAsync(
                activation.Endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "hello" })));

        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        Assert.Empty(fixture.Catalog.GetSnapshot().Providers);
        Assert.Equal(PackageReadinessState.Failed, fixture.Owner.State.GetSessionPackage("process.test")!.Readiness);
        Assert.Equal(PackageFailureOrigin.RuntimeRpcProvider, fixture.Owner.State.GetSessionPackage("process.test")!.FailureOrigin);
    }

    [Fact]
    public async Task Broker_PermissionRevocationCancelsFakeWorkerCallWithoutFaultingProcess()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        var activation = await fixture.PublishBrokerSessionAsync();
        var call = activation.Client.InvokeAsync(
            activation.Endpoint,
            "messages",
            "wait",
            JsonSerializer.SerializeToElement(new { message = "wait" })).AsTask();
        await Task.Delay(100);

        await fixture.SetInvokePermissionAsync(RuntimeRpcPermissionState.Denied);

        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () => await call);
        Assert.Equal(SunderRpcErrorKind.Cancelled, failure.Error.Kind);
        Assert.Single(fixture.Catalog.GetSnapshot().Providers);
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("process.test")!.Readiness);
    }

    [Fact]
    public async Task Broker_ForwardsSafeNestedRpcErrorWithoutFaultingProcess()
    {
        await using var fixture = await WorkerFixture.CreateAsync("forwarded-error");
        var activation = await fixture.PublishBrokerSessionAsync();

        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await activation.Client.InvokeAsync(
                activation.Endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "hello" })));

        Assert.Equal(SunderRpcErrorKind.PermissionDenied, failure.Error.Kind);
        Assert.Single(fixture.Catalog.GetSnapshot().Providers);
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("process.test")!.Readiness);
    }

    [Fact]
    public async Task SessionRetirement_DrainsFakeWorkerCallBeforeProcessShutdown()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        var activation = await fixture.PublishBrokerSessionAsync();
        var call = activation.Client.InvokeAsync(
            activation.Endpoint,
            "messages",
            "wait",
            JsonSerializer.SerializeToElement(new { message = "wait" })).AsTask();
        await Task.Delay(100);

        var retirement = fixture.Owner.State.ClearActiveSessionAsync();
        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () => await call);
        await retirement;

        Assert.Equal(SunderRpcErrorKind.Cancelled, failure.Error.Kind);
        await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HostStop_ShutsDownProcessActivationWithoutRestart()
    {
        using var hostStopping = new CancellationTokenSource();
        await using var fixture = await WorkerFixture.CreateAsync(
            "normal",
            hostStopping: hostStopping.Token);
        await fixture.StartAndActivateAsync();

        hostStopping.Cancel();

        await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly RuntimeRpcCatalog _catalog;
        private readonly RuntimeRpcPermissionStore _permissions;
        private readonly RuntimeSessionOwner _owner;
        private readonly RuntimePackageContext _context;
        private readonly RuntimeContentTransferStore _transfers;

        private WorkerFixture(
            string root,
            PreparedRuntimePackage prepared,
            Guid activationId,
            ProcessRuntimeWorker worker,
            RuntimeRpcCatalog catalog,
            RuntimeRpcPermissionStore permissions,
            RuntimeSessionOwner owner,
            RuntimePackageContext context,
            RuntimeContentTransferStore transfers)
        {
            _root = root;
            Prepared = prepared;
            ActivationId = activationId;
            Worker = worker;
            _catalog = catalog;
            _permissions = permissions;
            _owner = owner;
            _context = context;
            _transfers = transfers;
        }

        public PreparedRuntimePackage Prepared { get; }
        public Guid ActivationId { get; }
        public ProcessRuntimeWorker Worker { get; }
        public RuntimeRpcCatalog Catalog => _catalog;
        public RuntimeSessionOwner Owner => _owner;
        public RuntimeContentTransferStore Transfers => _transfers;

        public static async Task<WorkerFixture> CreateAsync(
            string mode,
            RuntimeProcessPolicyOptions? policy = null,
            ILogger? logger = null,
            CancellationToken hostStopping = default)
        {
            var root = Path.Combine(Path.GetTempPath(), "sunder-process-worker-tests", Guid.NewGuid().ToString("N"));
            var shadow = Path.Combine(root, "projection");
            Directory.CreateDirectory(shadow);
            foreach (var fixtureFile in Directory.EnumerateFiles(GetTestWorkerOutput()))
            {
                File.Copy(fixtureFile, Path.Combine(shadow, Path.GetFileName(fixtureFile)));
            }
            await File.WriteAllTextAsync(Path.Combine(shadow, "mode.txt"), mode);
            var entryPoint = Path.Combine(shadow, "Sunder.Runtime.Host.TestWorker.dll");
            var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
            var target = new SunderPackageTargetManifest
            {
                Role = SunderPackageFormat.RuntimeHostRole,
                Rid = rid,
                Kind = SunderPackageFormat.ProcessTargetKind,
                EntryPoint = Path.GetFileName(entryPoint),
                TargetFramework = "net10.0",
                SdkVersion = "1.1.0",
                RequiredHostCapabilities = ["rpc.v1", "sdk-baseline-1-1.v1"],
            };
            var manifest = new SunderPackageManifest
            {
                Id = "process.test",
                Name = "Process Test",
                Version = "1.0.0",
                Targets = [target],
                ContractBundles =
                [
                    new SunderPackageContractBundleManifest
                    {
                        ContractId = Contract.ContractId,
                        Version = Contract.Version,
                        DescriptorPath = "contracts/example.rpc.json",
                        Sha256 = Contract.Sha256,
                    },
                ],
                UsesContracts = [],
                Provides = [ProviderManifest()],
            };
            var key = new SunderPackageTargetKey(SunderPackageFormat.RuntimeHostRole, rid);
            var source = new RuntimePackageSource(
                "process.test",
                PackageSourceKind.Dev,
                shadow,
                Manifest: manifest,
                SelectedTargetKey: key,
                SelectedTarget: target);
            var prepared = new PreparedRuntimePackage(
                shadow,
                source,
                shadow,
                shadow,
                "process.test",
                "1.0.0",
                PackageHostRoles.Runtime,
                new RuntimePackageActivationState("process.test", "Process Test", "1.0.0", PackageHostRoles.Runtime, null, key, target),
                entryPoint,
                key,
                target,
                [],
                new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal)
                {
                    [PackageSessionPreparer.ContractKey(Contract.ContractId, Contract.Version)] = Contract,
                },
                new string('a', 64))
            {
                SessionId = Guid.NewGuid().ToString("N"),
                DevProcessLaunch = new DevProcessLaunchMetadata(
                    1,
                    DevProcessLaunchMetadata.NodeKind,
                    "process.test",
                    "1.0.0",
                    rid,
                    target.EntryPoint,
                    ResolveDotnetHost(),
                    "24.0.0",
                    new string('b', 64)),
            };
            var paths = new RuntimePackagePaths(Path.Combine(root, "runtime-data"));
            var catalog = new RuntimeRpcCatalog();
            var owner = new RuntimeSessionOwner(
                NullLogger<RuntimeSessionOwner>.Instance,
                new RuntimeEventStreamService(),
                rpcCatalog: catalog);
            var permissions = new RuntimeRpcPermissionStore(paths);
            var transfers = new RuntimeContentTransferStore(paths);
            var broker = new RuntimeRpcBroker(
                catalog,
                permissions,
                owner.State,
                owner,
                hostStopping: CancellationToken.None,
                policy: null,
                timeProvider: null);
            var activationId = Guid.NewGuid();
            var context = new RuntimePackageContext("process.test", "1.0.0", shadow, paths.PackageDataRootPath);
            var contentClient = new RuntimeRpcContentClient(
                transfers,
                owner.State,
                new RuntimeTransportPolicyOptions(),
                "process.test",
                activationId);
            var worker = new ProcessRuntimeWorker(
                logger ?? NullLogger.Instance,
                prepared,
                context,
                activationId,
                broker,
                policy,
                hostStopping,
                contentClient);
            return new WorkerFixture(root, prepared, activationId, worker, catalog, permissions, owner, context, transfers);
        }

        public async Task StartAndActivateAsync()
        {
            await Worker.StartAsync();
            await Worker.CommitGenerationAsync(new Sunder.Sdk.Abstractions.PackageRuntimeGeneration(ActivationId, 1));
            Worker.ActivateGeneration();
        }

        public async Task<BrokerActivation> PublishBrokerSessionAsync()
        {
            await Worker.StartAsync();
            var processManifest = Prepared.Source.Manifest!;
            var processContext = _context;
            var declaration = Assert.Single(processManifest.Provides!)!;
            var processPackage = new ActiveLoadedPackage(
                new ActivePackageDescriptor(
                    "process.test",
                    "Process Test",
                    "1.0.0",
                    PackageHostRoles.Runtime,
                    null,
                    true,
                    PackageReadinessState.Ready,
                    []),
                Prepared.Source,
                SettingsSchema: null,
                processContext.Storage.State,
                processContext.SecretsStore,
                AuthHandler: null,
                CallbackHandlers: new Dictionary<string, Sunder.Sdk.Abstractions.IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase),
                BackgroundServices: [Worker],
                new ServiceCollection().BuildServiceProvider(),
                LoadContext: null,
                processContext.Settings)
            {
                RuntimeActivationId = ActivationId,
                RpcContracts = Prepared.RpcContracts!,
                RpcProviders = new Dictionary<string, RuntimeRpcProviderRegistration>(StringComparer.Ordinal)
                {
                    [declaration.ProviderId!] = new RuntimeRpcProviderRegistration(
                        declaration.ProviderId!,
                        declaration.ContractId!,
                        declaration.ContractVersion!,
                        declaration.ContractSha256!,
                        Contract,
                        new ProcessRpcProviderHandler(Worker, declaration.ProviderId!),
                        declaration),
                },
                RpcManifestSha256 = new string('a', 64),
            };

            var callerManifest = new SunderPackageManifest
            {
                Id = "caller.package",
                Name = "Caller Package",
                Version = "1.0.0",
                ContractBundles = processManifest.ContractBundles,
                UsesContracts =
                [
                    new SunderPackageContractUseManifest
                    {
                        ContractId = Contract.ContractId,
                        VersionRange = Contract.Version,
                        Required = true,
                        Actions = ["discover", "invoke", "subscribe"],
                    },
                ],
                Provides = [],
            };
            var callerContext = new RuntimePackageContext(
                "caller.package",
                "1.0.0",
                Prepared.ShadowFolder,
                Path.Combine(_root, "caller-data"));
            var caller = new ActiveLoadedPackage(
                new ActivePackageDescriptor(
                    "caller.package",
                    "Caller Package",
                    "1.0.0",
                    PackageHostRoles.Runtime,
                    null,
                    true,
                    PackageReadinessState.Ready,
                    []),
                new RuntimePackageSource("caller.package", PackageSourceKind.Installed, Prepared.ShadowFolder, Manifest: callerManifest),
                SettingsSchema: null,
                callerContext.Storage.State,
                callerContext.SecretsStore,
                AuthHandler: null,
                CallbackHandlers: new Dictionary<string, Sunder.Sdk.Abstractions.IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase),
                BackgroundServices: [],
                new ServiceCollection().BuildServiceProvider(),
                LoadContext: null,
                callerContext.Settings)
            {
                RuntimeActivationId = Guid.NewGuid(),
                RpcContracts = Prepared.RpcContracts!,
                RpcContractUses = callerManifest.UsesContracts!.Select(static use => use!).ToArray(),
                RpcManifestSha256 = new string('a', 64),
            };
            var packages = new[] { caller, processPackage };
            var session = new ActivePackageSession(
                sessionFolder: null,
                packages.ToDictionary(static package => package.Descriptor.PackageId, StringComparer.OrdinalIgnoreCase),
                packages.ToDictionary(
                    static package => package.Descriptor.PackageId,
                    static package => new SessionPackageDescriptor(
                        package.Descriptor.PackageId,
                        package.Descriptor.PackageId,
                        package.Descriptor.Version,
                        PackageHostRoles.Runtime,
                        null,
                        true,
                        PackageReadinessState.Ready,
                        [],
                        null,
                        null,
                        null,
                        0),
                    StringComparer.OrdinalIgnoreCase),
                backgroundServicesStarted: true);
            await _owner.PublishAsync(
                session,
                _owner.Sources.Snapshot(),
                [],
                [],
                _owner.Generation);
            await Worker.CommitGenerationAsync(new Sunder.Sdk.Abstractions.PackageRuntimeGeneration(
                ActivationId,
                _owner.Generation));
            _catalog.ActivateSession(session, _owner.Generation);
            Worker.ActivateGeneration();
            await _permissions.SetAsync(
                "caller.package",
                "1.0.0",
                new string('a', 64),
                Contract.ContractId,
                SunderRpcProtocol.DiscoverAction,
                RuntimeRpcPermissionState.Granted,
                CancellationToken.None);
            await SetInvokePermissionAsync(RuntimeRpcPermissionState.Granted);
            var client = new RuntimeRpcClient(
                new RuntimeRpcBroker(
                    _catalog,
                    _permissions,
                    _owner.State,
                    _owner,
                    hostStopping: CancellationToken.None,
                    policy: null,
                    timeProvider: null),
                new RuntimeRpcCallerStamp("caller.package", caller.RuntimeActivationId));
            var endpoint = Assert.Single((await client.DiscoverAsync(Contract.ContractId)).Providers).Endpoint;
            return new BrokerActivation(client, endpoint);
        }

        public Task SetInvokePermissionAsync(RuntimeRpcPermissionState state)
            => _permissions.SetAsync(
                "caller.package",
                "1.0.0",
                new string('a', 64),
                Contract.ContractId,
                SunderRpcProtocol.InvokeAction,
                state,
                CancellationToken.None);

        public async Task<SunderRpcContentReference> RegisterCallerContentAsync(string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            await using var source = new MemoryStream(bytes, writable: false);
            return await _transfers.RegisterRpcContentAsync(
                source,
                bytes.Length,
                "text/plain",
                "caller.txt",
                "caller.package",
                "process.test",
                _owner.Generation,
                DateTimeOffset.UtcNow.AddMinutes(1),
                SunderRpcContentRepeatability.SingleUse,
                maximumUses: 1);
        }

        public SunderRpcInvocationContext InvocationContext()
            => new(
                "caller.package",
                "1.0.0",
                new SunderRpcProviderSnapshot(
                    "process.test",
                    "1.0.0",
                    "example.provider",
                    "example.rpc",
                    "1.0.0",
                    new string('a', 64),
                    ActivationId,
                    1,
                    1,
                    new SunderRpcEndpointReference("rpc1_process"),
                    1,
                    SunderRpcProviderState.Active),
                DateTimeOffset.UtcNow.AddMinutes(1),
                1,
                CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Worker.DisposeAsync();
            }
            catch
            {
            }
            _catalog.Dispose();
            _permissions.Dispose();
            _transfers.Dispose();
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }

        private static string GetTestWorkerOutput()
            => Path.Combine(
                FindRepositoryRoot(),
                "tests",
                "Sunder.Runtime.Host.TestWorker",
                "bin",
                ResolveConfiguration(),
                "net10.0");

        private static string ResolveDotnetHost()
        {
            var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
            if (Environment.ProcessPath is { } processPath
                && string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return processPath;
            }
            foreach (var candidate in new[]
                     {
                         "/usr/local/share/dotnet/dotnet",
                         "/usr/share/dotnet/dotnet",
                         @"C:\Program Files\dotnet\dotnet.exe",
                     })
            {
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("Could not resolve the dotnet Host for the fake process worker.");
        }

        private static string ResolveConfiguration()
            => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent?.Name
               ?? "Debug";

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate the Sunder Core repository root.");
        }
    }

    private static SunderPackageProviderManifest ProviderManifest()
        => new()
        {
            ProviderId = "example.provider",
            ContractId = Contract.ContractId,
            ContractVersion = Contract.Version,
            ContractSha256 = Contract.Sha256,
            Role = SunderPackageFormat.RuntimeHostRole,
        };

    private static SunderRpcContractDescriptor Contract { get; } = SunderRpcContractDescriptor.Parse(
        System.Text.Encoding.UTF8.GetBytes("""
        {
          "descriptorVersion":1,
          "contractId":"example.rpc",
          "version":"1.0.0",
          "services":[{"serviceId":"messages","methods":[
            {"methodId":"send","kind":"unary","requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Response"}},
            {"methodId":"wait","kind":"unary","requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Response"}},
            {"methodId":"content-roundtrip","kind":"unary","requestSchema":{"$ref":"#/$defs/ContentRequest"},"responseSchema":{"$ref":"#/$defs/ContentResponse"}},
            {"methodId":"watch","kind":"server-stream","requestSchema":{"$ref":"#/$defs/Request"},"eventSchema":{"$ref":"#/$defs/Response"}}
          ]}],
          "$defs":{
            "Request":{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":128}},"required":["message"],"additionalProperties":false},
            "Response":{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false},
            "ContentReference":{"type":"object","properties":{"id":{"type":"string","minLength":1,"maxLength":128},"length":{"type":"integer","minimum":0,"maximum":67108864},"sha256":{"type":"string","minLength":64,"maxLength":64},"mediaType":{"type":"string","minLength":1,"maxLength":128},"fileName":{"type":"string","minLength":1,"maxLength":255},"expiresAtUtc":{"type":"string","minLength":1,"maxLength":64},"repeatability":{"type":"string","minLength":1,"maxLength":32,"enum":["single-use","repeatable"]}},"required":["id","length","sha256","mediaType","fileName","expiresAtUtc","repeatability"],"additionalProperties":false},
            "ContentRequest":{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":128},"content":{"$ref":"#/$defs/ContentReference"}},"required":["message","content"],"additionalProperties":false},
            "ContentResponse":{"type":"object","properties":{"accepted":{"type":"boolean"},"content":{"$ref":"#/$defs/ContentReference"},"openedFileDiscarded":{"type":"boolean"}},"required":["accepted","content","openedFileDiscarded"],"additionalProperties":false}
          }
        }
        """));

    private static SunderRpcContentReference ReadContentReference(JsonElement value)
        => new(
            value.GetProperty("id").GetString()!,
            value.GetProperty("length").GetInt64(),
            value.GetProperty("sha256").GetString()!,
            value.GetProperty("mediaType").GetString()!,
            value.GetProperty("fileName").GetString()!,
            value.GetProperty("expiresAtUtc").GetDateTimeOffset(),
            value.GetProperty("repeatability").GetString() == "single-use"
                ? SunderRpcContentRepeatability.SingleUse
                : SunderRpcContentRepeatability.Repeatable);

    private sealed record BrokerActivation(RuntimeRpcClient Client, SunderRpcEndpointReference Endpoint);

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
