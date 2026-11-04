using System.Reflection;
using System.Text.Json;
using Sunder.App.Bridge;
using Sunder.App.Services;
using Sunder.Sdk.Avalonia.Hosting;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppBridgeServiceChannelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryReconstructsEmptyAndPopulatedCatalogsAcrossTheBridge(bool populated)
    {
        var provider = new SunderRpcProviderSnapshot("example.provider", "2.0.0", "provider", "example.contract", "3.0.0",
            new string('a', 64), new string('b', 64), Guid.NewGuid(), 7, 11, new("opaque-endpoint"), 13,
            SunderRpcProviderState.Faulted, "rpc.provider-faulted");
        var original = new SunderRpcCatalogSnapshot(13, 17, populated ? [provider] : [], resetRequired: populated);
        var rpc = DispatchProxy.Create<ISunderRpcClient, DiscoveryRpc>();
        var source = (DiscoveryRpc)(object)rpc;
        source.Snapshot = original;
        using var cancellation = new CancellationTokenSource();
        await using var host = new AppBridgeServiceChannel(rpc);
        var sdk = new AppRpcClient(new AppChannel(host));

        var snapshot = await sdk.DiscoverAsync("example.contract", cancellation.Token);

        Assert.Equal("example.contract", source.ContractId);
        Assert.Equal(cancellation.Token, source.Token);
        Assert.NotSame(original, snapshot);
        Assert.Equal(original.Revision, snapshot.Revision);
        Assert.Equal(original.Sequence, snapshot.Sequence);
        Assert.Equal(original.ResetRequired, snapshot.ResetRequired);
        Assert.Equal(original.Providers, snapshot.Providers);
        if (populated)
        {
            var decoded = Assert.Single(snapshot.Providers);
            Assert.NotSame(provider, decoded);
            Assert.NotSame(provider.Endpoint, decoded.Endpoint);
        }
        Assert.True(Assert.IsAssignableFrom<ICollection<SunderRpcProviderSnapshot>>(snapshot.Providers).IsReadOnly);
    }

    [Theory]
    [InlineData("construction")]
    [InlineData("enumerator")]
    [InlineData("move")]
    [InlineData("current")]
    [InlineData("dispose")]
    public async Task WatchFailuresAtEveryBoundaryReconstructPackageLocalRpcEvidence(string stage)
    {
        var original = Failure(true);
        var rpc = DispatchProxy.Create<ISunderRpcClient, WatchRpc>();
        ((WatchRpc)(object)rpc).Sequence = new FaultingSequence(stage, original);
        await using var host = new AppBridgeServiceChannel(rpc);
        var sdk = new AppChannel(host);
        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () =>
        {
            await foreach (var _ in sdk.Watch<object>("rpc.watch", new { afterRevision = 0, afterSequence = 0 }, default)) break;
        });
        Check(failure, original, true);
    }

    [Fact]
    public async Task AggregateDisposalFailuresReconstructPackageLocalRpcEvidence()
    {
        var original = Failure(true);
        var host = new AppBridgeServiceChannel(content: SunderContent.FromStream(new FaultingStream(original)));
        var error = await Assert.ThrowsAsync<AggregateException>(() => new AppChannel(host).DisposeAsync().AsTask());
        Check(Assert.IsType<SunderRpcException>(Assert.Single(error.Flatten().InnerExceptions)), original, true);
    }

    [Fact]
    public async Task LoggingDoesNotFormatDisabledEntriesOrSerializeArbitraryPackageObjectGraphs()
    {
        await using var host = new LoggingChannel();
        var logging = new AppLoggingAdapter(new AppChannel(host));
        logging.LoggerFactory.CreateLogger("test").Log(Microsoft.Extensions.Logging.LogLevel.None, default, "state", null,
            static (_, _) => throw new InvalidOperationException("Disabled formatter was called."));
        await logging.Events.WriteAsync(Sunder.Sdk.Logging.PackageLogLevel.Information, "test", "message",
            new Dictionary<string, object?> { ["value"] = new LogValue() });
        Assert.Equal("package-value", host.Entry.GetProperty("attributes").GetProperty("value").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RpcEvidenceSurvivesCallsAndStreamFailuresWithoutLeakingTheOriginalException(bool authenticated)
    {
        var error = Failure(authenticated);
        await using var host = new FaultingChannel(error);
        var sdk = new AppChannel(host);
        Check(await Assert.ThrowsAsync<SunderRpcException>(() => sdk.Call("fail", null, default).AsTask()), error, authenticated);
        Check(Assert.Throws<SunderRpcException>(() => sdk.Get<object>("fail")), error, authenticated);
        var stream = (await sdk.Read("read", null, default))!;
        Check(Assert.Throws<SunderRpcException>(() => stream.ReadByte()), error, authenticated);
        Check(await Assert.ThrowsAsync<SunderRpcException>(() => stream.ReadAsync(new byte[1].AsMemory()).AsTask()), error, authenticated);
        Check(await Assert.ThrowsAsync<SunderRpcException>(() => stream.DisposeAsync().AsTask()), error, authenticated);
    }

    [Fact]
    public async Task InputStreamFailureCannotMintHostEvidenceOrTransferStreamOwnership()
    {
        await using var host = new FaultingChannel(Failure(false));
        var sdk = new AppChannel(host);
        var input = new FaultingStream(Failure(true));
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => sdk.Write<object>("write", null, input, default).AsTask());
        Assert.False(IsAuthenticated(error));
        Assert.Equal(0, input.Disposals);
    }

    [Fact]
    public async Task ParentRetirementWaitsForPendingAcquisitionAndDisposesTheRejectedChildExactlyOnce()
    {
        var stream = new DelayedDisposalStream();
        var child = new AppBridgeServiceChannel(content: SunderContent.FromStream(stream));
        var host = new DelayedOpenChannel(child);
        var opening = host.OpenAsync("open", default, default).AsTask();
        await host.Entered.Task;
        var retirement = host.DisposeAsync().AsTask();
        Assert.False(retirement.IsCompleted);
        host.Release.SetResult();
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(retirement.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.OpenAsync("open", default, default).AsTask());
        stream.Release.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => opening);
        await retirement;
        await host.DisposeAsync();
        Assert.Equal(1, stream.Disposals);
    }

    private static SunderRpcException Failure(bool authenticated)
    {
        var error = new SunderRpcError(SunderRpcErrorKind.Unavailable, "rpc.test-unavailable", "Unavailable",
            SunderRpcDispatchState.NotDispatched, SunderRpcRetrySafety.Safe);
        // Produce both transport-authenticated and ordinary failures without granting
        // test fixtures a production friend relationship with the SDK.
        return (SunderRpcException)Activator.CreateInstance(typeof(SunderRpcException), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [error, authenticated], null)!;
    }
    private static bool IsAuthenticated(SunderRpcException error)
        => (bool)typeof(SunderRpcException).GetProperty("IsHostAuthenticated", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(error)!;
    private static void Check(SunderRpcException actual, SunderRpcException original, bool authenticated)
    {
        Assert.NotSame(original, actual);
        Assert.Equal(original.Error.Code, actual.Error.Code);
        Assert.Equal(original.Error.DispatchState, actual.Error.DispatchState);
        Assert.Equal(original.Error.RetrySafety, actual.Error.RetrySafety);
        Assert.Equal(authenticated, IsAuthenticated(actual));
    }
    private sealed class FaultingChannel(SunderRpcException error) : AppBridgeServiceChannel
    {
        protected override JsonElement GetCore(string operation, JsonElement arguments) => throw error;
        protected override ValueTask<JsonElement> InvokeCoreAsync(string operation, JsonElement arguments, CancellationToken token) => throw error;
        protected override ValueTask<Stream?> ReadCoreAsync(string operation, JsonElement arguments, CancellationToken token)
            => ValueTask.FromResult<Stream?>(new FaultingStream(error));
        protected override async ValueTask<JsonElement> WriteCoreAsync(string operation, JsonElement arguments, Stream input, CancellationToken token)
        { return Encode(await input.ReadAsync(new byte[1].AsMemory(), token)); }
    }
    public class DiscoveryRpc : DispatchProxy
    {
        internal SunderRpcCatalogSnapshot Snapshot { get; set; } = null!;
        internal string? ContractId;
        internal CancellationToken Token;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            Assert.Equal(nameof(ISunderRpcClient.DiscoverAsync), method!.Name);
            ContractId = (string)arguments![0]!;
            Token = (CancellationToken)arguments[1]!;
            return ValueTask.FromResult(Snapshot);
        }
    }
    public class WatchRpc : DispatchProxy
    {
        internal FaultingSequence Sequence { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
            => method!.Name == nameof(ISunderRpcClient.WatchAsync) ? Sequence.Create() : throw new NotSupportedException();
    }
    internal sealed class FaultingSequence(string stage, Exception error) : IAsyncEnumerable<SunderRpcCatalogEvent>, IAsyncEnumerator<SunderRpcCatalogEvent>
    {
        internal IAsyncEnumerable<SunderRpcCatalogEvent> Create() => stage == "construction" ? throw error : this;
        public IAsyncEnumerator<SunderRpcCatalogEvent> GetAsyncEnumerator(CancellationToken token = default) => stage == "enumerator" ? throw error : this;
        public ValueTask<bool> MoveNextAsync() => stage == "move" ? throw error : ValueTask.FromResult(true);
        public SunderRpcCatalogEvent Current => stage == "current" ? throw error : null!;
        public ValueTask DisposeAsync() => stage == "dispose" ? ValueTask.FromException(error) : ValueTask.CompletedTask;
    }
    private sealed class LoggingChannel : AppBridgeServiceChannel
    {
        internal JsonElement Entry;
        protected override JsonElement GetCore(string operation, JsonElement arguments)
            => operation == "logging.enabled" ? Encode(false) : throw new InvalidOperationException(operation);
        protected override ValueTask<JsonElement> InvokeCoreAsync(string operation, JsonElement arguments, CancellationToken token)
        { Assert.Equal("logging.event", operation); Entry = arguments; return ValueTask.FromResult(Encode<object?>(null)); }
    }
    private sealed class LogValue
    {
        public object Value => throw new InvalidOperationException("Logging inspected a package object.");
        public override string ToString() => "package-value";
    }
    private sealed class FaultingStream(Exception error) : MemoryStream
    {
        internal int Disposals;
        public override int Read(byte[] buffer, int offset, int count) => throw error;
        public override int Read(Span<byte> buffer) => throw error;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(error);
        public override ValueTask DisposeAsync() { Disposals++; return ValueTask.FromException(error); }
    }
    private sealed class DelayedOpenChannel(AppBridgeServiceChannel child) : AppBridgeServiceChannel
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async ValueTask<AppBridgeServiceChannel> OpenCoreAsync(string operation, JsonElement arguments, CancellationToken token)
        { Entered.SetResult(); await Release.Task; return child; }
    }
    private sealed class DelayedDisposalStream : MemoryStream
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposals;
        public override async ValueTask DisposeAsync() { Disposals++; Entered.SetResult(); await Release.Task; await base.DisposeAsync(); }
    }
}
