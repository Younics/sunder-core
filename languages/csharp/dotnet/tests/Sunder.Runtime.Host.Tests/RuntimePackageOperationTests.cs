using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimePackageOperationTests
{
    private const string RpcFailureCode = "rpc.provider.handler-fault";
    private const string RpcFailureMessage = "provider-secret=do-not-persist";
    private static readonly Guid RpcProviderActivationId = Guid.NewGuid();
    private static readonly PackageRuntimeOperation<EchoRequest, EchoResponse> EchoOperation = new("echo.run");
    private static readonly PackageRuntimeStream<CountRequest, CountEvent> CountStream = new("count.events");

    [Theory]
    [InlineData("")]
    [InlineData("Echo")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    public void OperationDescriptor_RejectsInvalidIds(string operationId)
    {
        Assert.Throws<ArgumentException>(() => new PackageRuntimeOperation<EchoRequest, EchoResponse>(operationId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Events")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    public void StreamDescriptor_RejectsInvalidIds(string streamId)
    {
        Assert.Throws<ArgumentException>(() => new PackageRuntimeStream<CountRequest, CountEvent>(streamId));
    }

    [Fact]
    public async Task OperationService_InvokesTypedHandlerInActivePackageSession()
    {
        const string packageId = "test.package";
        var services = new ServiceCollection().BuildServiceProvider();
        var registry = new RuntimePackageContributionRegistry(
            services,
            packageId);
        registry.RegisterRuntimeOperation(EchoOperation, new EchoHandler());
        var state = new PackageSessionState(NullLogger.Instance, () => { }, _ => { });
        await state.PublishSessionAsync(CreateSession(packageId, registry.RuntimeOperations));
        var service = new RuntimePackageOperationService(state);

        var responseBytes = await service.InvokeAsync(
            packageId,
            EchoOperation.OperationId,
            JsonSerializer.SerializeToUtf8Bytes(new EchoRequest("hello"), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var response = JsonSerializer.Deserialize<EchoResponse>(responseBytes, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("HELLO", response?.Value);
        await state.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task OperationService_RpcFailurePersistsOnlySafeTypedDiagnostics()
    {
        const string packageId = "test.package";
        var eventLogger = new RecordingEventLogger();
        var serviceProvider = new ServiceCollection()
            .AddSingleton<IPackageContext>(new TestPackageContext(packageId, eventLogger))
            .BuildServiceProvider();
        var registry = new RuntimePackageContributionRegistry(serviceProvider, packageId);
        registry.RegisterRuntimeOperation(EchoOperation, new RpcFailureHandler());
        var state = new PackageSessionState(NullLogger.Instance, () => { }, _ => { });
        await state.PublishSessionAsync(CreateSession(
            packageId,
            registry.RuntimeOperations,
            serviceProvider: serviceProvider));
        var service = new RuntimePackageOperationService(state);

        await Assert.ThrowsAsync<AggregateException>(async () =>
            await service.InvokeAsync(
                packageId,
                EchoOperation.OperationId,
                JsonSerializer.SerializeToUtf8Bytes(new EchoRequest("hello")),
                CancellationToken.None));

        var entry = Assert.Single(eventLogger.Entries);
        Assert.Equal("runtime.operation.rpc-failed", entry.EventName);
        Assert.Equal(EchoOperation.OperationId, entry.Attributes["runtime.operation_id"]);
        Assert.Equal(nameof(SunderRpcErrorKind.ProviderFaulted), entry.Attributes["rpc.error_kind"]);
        Assert.Equal(RpcFailureCode, entry.Attributes["rpc.error_code"]);
        Assert.Equal(packageId, entry.Attributes["rpc.caller_package_id"]);
        Assert.Equal("provider.package", entry.Attributes["rpc.provider_package_id"]);
        Assert.Equal("2.3.4", entry.Attributes["rpc.provider_package_version"]);
        Assert.Equal("example.provider", entry.Attributes["rpc.provider_id"]);
        Assert.Equal("example.rpc", entry.Attributes["rpc.contract_id"]);
        Assert.Equal(RpcProviderActivationId, entry.Attributes["rpc.provider_activation_id"]);
        Assert.Equal("messages", entry.Attributes["rpc.service_id"]);
        Assert.Equal("send", entry.Attributes["rpc.method_id"]);
        Assert.Equal(typeof(SecretBearingProviderException).FullName, entry.Attributes["rpc.exception_type"]);
        Assert.Equal(
            RuntimeRpcFailureContextStore.CreateExceptionFingerprint(
                typeof(SecretBearingProviderException).FullName!),
            entry.Attributes["rpc.exception_fingerprint"]);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(RpcFailureMessage, entry.Message, StringComparison.Ordinal);
        Assert.All(entry.Attributes.Values, value =>
            Assert.DoesNotContain(RpcFailureMessage, value?.ToString() ?? string.Empty, StringComparison.Ordinal));
        await state.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task OperationService_ForgedRpcFailureCannotPersistHostContext()
    {
        const string packageId = "test.package";
        var eventLogger = new RecordingEventLogger();
        var serviceProvider = new ServiceCollection()
            .AddSingleton<IPackageContext>(new TestPackageContext(packageId, eventLogger))
            .BuildServiceProvider();
        var registry = new RuntimePackageContributionRegistry(serviceProvider, packageId);
        registry.RegisterRuntimeOperation(EchoOperation, new ForgedRpcFailureHandler());
        var state = new PackageSessionState(NullLogger.Instance, () => { }, _ => { });
        await state.PublishSessionAsync(CreateSession(
            packageId,
            registry.RuntimeOperations,
            serviceProvider: serviceProvider));
        var service = new RuntimePackageOperationService(state);

        await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await service.InvokeAsync(
                packageId,
                EchoOperation.OperationId,
                JsonSerializer.SerializeToUtf8Bytes(new EchoRequest("hello")),
                CancellationToken.None));

        Assert.Empty(eventLogger.Entries);
        await state.ClearActiveSessionAsync();
    }

    [Fact]
    public void ContributionRegistry_RejectsDuplicateOperationId()
    {
        var registry = new RuntimePackageContributionRegistry(
            new ServiceCollection().BuildServiceProvider(),
            "test.package");
        registry.RegisterRuntimeOperation(EchoOperation, new EchoHandler());

        Assert.Throws<InvalidOperationException>(() =>
            registry.RegisterRuntimeOperation(EchoOperation, new EchoHandler()));
    }

    [Fact]
    public async Task OperationService_StreamsTypedEventsWhileSessionLeaseIsActive()
    {
        const string packageId = "test.package";
        var registry = new RuntimePackageContributionRegistry(
            new ServiceCollection().BuildServiceProvider(),
            packageId);
        registry.RegisterRuntimeStream(CountStream, new CountHandler());
        var state = new PackageSessionState(NullLogger.Instance, () => { }, _ => { });
        await state.PublishSessionAsync(CreateSession(packageId, streams: registry.RuntimeStreams));
        var service = new RuntimePackageOperationService(state);
        var events = new List<CountEvent>();

        await foreach (var eventBytes in service.SubscribeAsync(
                           packageId,
                           CountStream.StreamId,
                           JsonSerializer.SerializeToUtf8Bytes(new CountRequest(3), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                           CancellationToken.None))
        {
            events.Add(JsonSerializer.Deserialize<CountEvent>(eventBytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        }

        Assert.Equal([1, 2, 3], events.Select(value => value.Value));
        await state.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task StreamService_RpcFailurePersistsOnlySafeTypedDiagnostics()
    {
        const string packageId = "test.package";
        var eventLogger = new RecordingEventLogger();
        var serviceProvider = new ServiceCollection()
            .AddSingleton<IPackageContext>(new TestPackageContext(packageId, eventLogger))
            .BuildServiceProvider();
        var registry = new RuntimePackageContributionRegistry(serviceProvider, packageId);
        registry.RegisterRuntimeStream(CountStream, new RpcFailureStreamHandler());
        var state = new PackageSessionState(NullLogger.Instance, () => { }, _ => { });
        await state.PublishSessionAsync(CreateSession(
            packageId,
            streams: registry.RuntimeStreams,
            serviceProvider: serviceProvider));
        var service = new RuntimePackageOperationService(state);
        await using var enumerator = service.SubscribeAsync(
            packageId,
            CountStream.StreamId,
            JsonSerializer.SerializeToUtf8Bytes(new CountRequest(1)),
            CancellationToken.None).GetAsyncEnumerator();

        await Assert.ThrowsAsync<SunderRpcException>(() => enumerator.MoveNextAsync().AsTask());

        var entry = Assert.Single(eventLogger.Entries);
        Assert.Equal("runtime.stream.rpc-failed", entry.EventName);
        Assert.Equal(CountStream.StreamId, entry.Attributes["runtime.operation_id"]);
        Assert.Equal(nameof(SunderRpcErrorKind.ProviderFaulted), entry.Attributes["rpc.error_kind"]);
        Assert.Equal(RpcFailureCode, entry.Attributes["rpc.error_code"]);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(RpcFailureMessage, entry.Message, StringComparison.Ordinal);
        await state.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task StreamService_DisposeRpcFailurePersistsOnlySafeTypedDiagnostics()
    {
        const string packageId = "test.package";
        var eventLogger = new RecordingEventLogger();
        var serviceProvider = new ServiceCollection()
            .AddSingleton<IPackageContext>(new TestPackageContext(packageId, eventLogger))
            .BuildServiceProvider();
        var registry = new RuntimePackageContributionRegistry(serviceProvider, packageId);
        registry.RegisterRuntimeStream(CountStream, new RpcFailureOnDisposeStreamHandler());
        var state = new PackageSessionState(NullLogger.Instance, () => { }, _ => { });
        await state.PublishSessionAsync(CreateSession(
            packageId,
            streams: registry.RuntimeStreams,
            serviceProvider: serviceProvider));
        var service = new RuntimePackageOperationService(state);
        var enumerator = service.SubscribeAsync(
            packageId,
            CountStream.StreamId,
            JsonSerializer.SerializeToUtf8Bytes(new CountRequest(1)),
            CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        await Assert.ThrowsAsync<SunderRpcException>(() => enumerator.DisposeAsync().AsTask());

        var entry = Assert.Single(eventLogger.Entries);
        Assert.Equal("runtime.stream.rpc-failed", entry.EventName);
        Assert.Equal(CountStream.StreamId, entry.Attributes["runtime.operation_id"]);
        Assert.Equal(nameof(SunderRpcErrorKind.ProviderFaulted), entry.Attributes["rpc.error_kind"]);
        Assert.Equal(RpcFailureCode, entry.Attributes["rpc.error_code"]);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(RpcFailureMessage, entry.Message, StringComparison.Ordinal);
        await state.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task InfiniteStream_RetirementCancellationAllowsReloadToComplete()
    {
        const string packageId = "test.package";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new RuntimePackageContributionRegistry(
            new ServiceCollection().BuildServiceProvider(),
            packageId);
        registry.RegisterRuntimeStream(CountStream, new CancellableInfiniteHandler(started));
        var state = new PackageSessionState(NullLogger.Instance, () => { }, _ => { });
        await state.PublishSessionAsync(CreateSession(packageId, streams: registry.RuntimeStreams));
        var service = new RuntimePackageOperationService(state);
        await using var enumerator = service.SubscribeAsync(
            packageId,
            CountStream.StreamId,
            JsonSerializer.SerializeToUtf8Bytes(new CountRequest(1)),
            CancellationToken.None).GetAsyncEnumerator();
        var next = enumerator.MoveNextAsync().AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var replacement = CreateSession("replacement.package");
        var publication = state.PublishSessionAsync(replacement);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        var result = await publication.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, result.Generation);
        await state.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task StreamIgnoringRetirement_FailsReloadAndKeepsSessionAdmissible()
    {
        const string packageId = "test.package";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new RuntimePackageContributionRegistry(
            new ServiceCollection().BuildServiceProvider(),
            packageId);
        registry.RegisterRuntimeStream(CountStream, new IgnoringCancellationHandler(started, release));
        var state = new PackageSessionState(
            NullLogger.Instance,
            () => { },
            _ => { },
            TimeSpan.FromMilliseconds(50));
        await state.PublishSessionAsync(CreateSession(packageId, streams: registry.RuntimeStreams));
        var service = new RuntimePackageOperationService(state);
        await using var enumerator = service.SubscribeAsync(
            packageId,
            CountStream.StreamId,
            JsonSerializer.SerializeToUtf8Bytes(new CountRequest(1)),
            CancellationToken.None).GetAsyncEnumerator();
        var next = enumerator.MoveNextAsync().AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var replacement = CreateSession("replacement.package");

        await Assert.ThrowsAsync<RuntimeUnavailableException>(
            () => state.PublishSessionAsync(replacement));
        using (var admitted = state.AcquireLease())
        {
            Assert.Equal(1, admitted.Generation);
            Assert.NotNull(state.GetLoadedPackage(admitted, packageId));
        }

        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => next.WaitAsync(TimeSpan.FromSeconds(2)));

        await using var admittedEnumerator = service.SubscribeAsync(
            packageId,
            CountStream.StreamId,
            JsonSerializer.SerializeToUtf8Bytes(new CountRequest(1)),
            CancellationToken.None).GetAsyncEnumerator();
        Assert.False(await admittedEnumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        await replacement.DisposeAsync();
        await state.ClearActiveSessionAsync();
    }

    [Fact]
    public void ContributionRegistry_RejectsDuplicateStreamId()
    {
        var registry = new RuntimePackageContributionRegistry(
            new ServiceCollection().BuildServiceProvider(),
            "test.package");
        registry.RegisterRuntimeStream(CountStream, new CountHandler());

        Assert.Throws<InvalidOperationException>(() =>
            registry.RegisterRuntimeStream(CountStream, new CountHandler()));
    }

    private static ActivePackageSession CreateSession(
        string packageId,
        IReadOnlyDictionary<string, RuntimePackageOperationRegistration>? operations = null,
        IReadOnlyDictionary<string, RuntimePackageStreamRegistration>? streams = null,
        IServiceProvider? serviceProvider = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-operation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assemblyPath = typeof(RuntimePackageOperationTests).Assembly.Location;
        var loadedPackage = new ActiveLoadedPackage(
            new ActivePackageDescriptor(packageId, packageId, "1.0.0", PackageHostRoles.Runtime, null, true, PackageReadinessState.Ready, []),
            new RuntimePackageSource(packageId, PackageSourceKind.Dev, root),
            SettingsSchema: null,
            new JsonPackageKeyValueStore(Path.Combine(root, "state.json")),
            new JsonPackageSecretsStore(
                Path.Combine(root, "secrets.json"),
                null,
                null,
                new RestrictedFileMasterKeyProtection()),
            AuthHandler: null,
            CallbackHandlers: new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase),
            BackgroundServices: [],
            serviceProvider ?? new ServiceCollection().BuildServiceProvider(),
            new RuntimePackageLoadContext(
                packageId,
                assemblyPath,
                new RuntimeSharedAssemblyRegistry([Path.GetDirectoryName(assemblyPath)!])),
            EmptyTestPackageSettings.Instance)
        {
            RuntimeOperations = operations ?? new Dictionary<string, RuntimePackageOperationRegistration>(StringComparer.Ordinal),
            RuntimeStreams = streams ?? new Dictionary<string, RuntimePackageStreamRegistration>(StringComparer.Ordinal),
        };
        var sessionPackage = new SessionPackageDescriptor(
            packageId,
            packageId,
            "1.0.0",
            PackageHostRoles.Runtime,
            null,
            true,
            PackageReadinessState.Ready,
            [],
            null,
            null,
            null,
            0);
        return new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = sessionPackage,
            });
    }

    private sealed record EchoRequest(string Value);

    private sealed record EchoResponse(string Value);

    private sealed record CountRequest(int Count);

    private sealed record CountEvent(int Value);

    private sealed class EchoHandler : IPackageRuntimeOperationHandler<EchoRequest, EchoResponse>
    {
        public ValueTask<EchoResponse> HandleAsync(EchoRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new EchoResponse(request.Value.ToUpperInvariant()));
        }
    }

    private sealed class RpcFailureHandler : IPackageRuntimeOperationHandler<EchoRequest, EchoResponse>
    {
        public ValueTask<EchoResponse> HandleAsync(EchoRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromException<EchoResponse>(new AggregateException(
                new InvalidOperationException(RpcFailureMessage),
                CreateRpcFailure()));
    }

    private sealed class ForgedRpcFailureHandler : IPackageRuntimeOperationHandler<EchoRequest, EchoResponse>
    {
        public ValueTask<EchoResponse> HandleAsync(EchoRequest request, CancellationToken cancellationToken = default)
        {
            var forged = new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.ProviderFaulted,
                RpcFailureCode,
                RpcFailureMessage));
            forged.Data["rpc.caller_package_id"] = "forged.package";
            forged.Data["rpc.provider_package_id"] = "forged.provider";
            return ValueTask.FromException<EchoResponse>(forged);
        }
    }

    private sealed class CountHandler : IPackageRuntimeStreamHandler<CountRequest, CountEvent>
    {
        public async IAsyncEnumerable<CountEvent> SubscribeAsync(
            CountRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var value = 1; value <= request.Count; value++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new CountEvent(value);
            }
        }
    }

    private sealed class RpcFailureStreamHandler : IPackageRuntimeStreamHandler<CountRequest, CountEvent>
    {
        public async IAsyncEnumerable<CountEvent> SubscribeAsync(
            CountRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (request.Count >= 0)
            {
                throw CreateRpcFailure();
            }
            yield break;
        }
    }

    private sealed class RpcFailureOnDisposeStreamHandler : IPackageRuntimeStreamHandler<CountRequest, CountEvent>
    {
        public async IAsyncEnumerable<CountEvent> SubscribeAsync(
            CountRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return new CountEvent(1);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                throw CreateRpcFailure();
            }
        }
    }

    private static SunderRpcException CreateRpcFailure()
        => RuntimeRpcFailureContextStore.AttachProviderFault(
            SunderRpcException.Infrastructure(
                SunderRpcErrorKind.ProviderFaulted,
                RpcFailureCode,
                "The provider handler violated the RPC invocation contract."),
            "test.package",
            new SunderRpcProviderSnapshot(
                "provider.package",
                "2.3.4",
                "example.provider",
                "example.rpc",
                "1.0.0",
                new string('a', 64),
                RpcProviderActivationId,
                1,
                1,
                new SunderRpcEndpointReference("rpc1_test"),
                1,
                SunderRpcProviderState.Faulted,
                RpcFailureCode),
            "messages",
            "send",
            new SecretBearingProviderException(RpcFailureMessage));

    private sealed class SecretBearingProviderException(string message) : Exception(message);

    private sealed class TestPackageContext(string packageId, IPackageEventLogger eventLogger) : IPackageContext
    {
        public string PackageId { get; } = packageId;
        public string Version => "1.0.0";
        public string ContentRootPath => string.Empty;
        public IPackageStorageContext Storage => throw new NotSupportedException();
        public IPackageSettings Settings => throw new NotSupportedException();
        public IPackageSecrets Secrets => throw new NotSupportedException();
        public IPackageLogging Logging { get; } = new TestPackageLogging(eventLogger);
    }

    private sealed class TestPackageLogging(IPackageEventLogger events) : IPackageLogging
    {
        public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;
        public IPackageEventLogger Events { get; } = events;
    }

    private sealed class RecordingEventLogger : IPackageEventLogger
    {
        public List<RecordedEvent> Entries { get; } = [];

        public ValueTask WriteAsync(
            PackageLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
        {
            Entries.Add(new RecordedEvent(
                eventName,
                message,
                attributes?.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal)
                    ?? new Dictionary<string, object?>(StringComparer.Ordinal),
                exception));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record RecordedEvent(
        string EventName,
        string Message,
        IReadOnlyDictionary<string, object?> Attributes,
        Exception? Exception);

    private sealed class CancellableInfiniteHandler(TaskCompletionSource started)
        : IPackageRuntimeStreamHandler<CountRequest, CountEvent>
    {
        public async IAsyncEnumerable<CountEvent> SubscribeAsync(
            CountRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class IgnoringCancellationHandler(
        TaskCompletionSource started,
        TaskCompletionSource release) : IPackageRuntimeStreamHandler<CountRequest, CountEvent>
    {
        public async IAsyncEnumerable<CountEvent> SubscribeAsync(
            CountRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await release.Task;
            yield break;
        }
    }
}
