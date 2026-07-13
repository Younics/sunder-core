using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimePackageOperationTests
{
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
            new RuntimePackageExtensionCatalog(),
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
    public void ContributionRegistry_RejectsDuplicateOperationId()
    {
        var registry = new RuntimePackageContributionRegistry(
            new ServiceCollection().BuildServiceProvider(),
            new RuntimePackageExtensionCatalog(),
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
            new RuntimePackageExtensionCatalog(),
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
    public async Task InfiniteStream_RetirementCancellationAllowsReloadToComplete()
    {
        const string packageId = "test.package";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new RuntimePackageContributionRegistry(
            new ServiceCollection().BuildServiceProvider(),
            new RuntimePackageExtensionCatalog(),
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
            new RuntimePackageExtensionCatalog(),
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
            new RuntimePackageExtensionCatalog(),
            "test.package");
        registry.RegisterRuntimeStream(CountStream, new CountHandler());

        Assert.Throws<InvalidOperationException>(() =>
            registry.RegisterRuntimeStream(CountStream, new CountHandler()));
    }

    private static ActivePackageSession CreateSession(
        string packageId,
        IReadOnlyDictionary<string, RuntimePackageOperationRegistration>? operations = null,
        IReadOnlyDictionary<string, RuntimePackageStreamRegistration>? streams = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-operation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assemblyPath = typeof(RuntimePackageOperationTests).Assembly.Location;
        var loadedPackage = new ActiveLoadedPackage(
            new ActivePackageDescriptor(packageId, packageId, "1.0.0", null, true, PackageReadinessState.Ready, []),
            new RuntimePackageSource(packageId, PackageSourceKind.Dev, root),
            ConfigurationSchema: null,
            new JsonPackageKeyValueStore(Path.Combine(root, "state.json")),
            new JsonPackageSecretsStore(
                Path.Combine(root, "secrets.json"),
                null,
                null,
                new RestrictedFileMasterKeyProtection()),
            AuthHandler: null,
            CallbackHandlers: new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase),
            BackgroundServices: [],
            new ServiceCollection().BuildServiceProvider(),
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
