using System.Text.Json;
using System.Threading.Channels;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpModelProjectionTests
{
    [Fact]
    public async Task ActualGeneratedStartRunsOnceAndItsPostInvokeMetadataErrorIsNeverNoDispatchEvidence()
    {
        const string declaration = """
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)]
            public interface BuilderService { ValueTask<int> StartAsync(CancellationToken token = default); }
            """;
        var prepass = SunderRpcCSharpOwnedClientPrepass.Compile(new(), new("example.dispatch", "1.0.0",
            [new("source.cs", declaration)], References(), GenerateServiceInterfaces: true));
        Assert.True(prepass.Compilation.Success, string.Join(Environment.NewLine, prepass.Compilation.Diagnostics));
        var clients = SunderRpcCSharpServiceApiEmitter.EmitAppClients(prepass.Compilation.Contracts, "example.dispatch", "1.0.0");
        Assert.All(clients, file => Assert.DoesNotContain("HasTrustedNoDispatchEvidence", file.Text, StringComparison.Ordinal));
        var (load, probe) = LoadProjectionProbe(prepass.Sources.Append(new SunderRpcCSharpSourceFile("source.cs", declaration))
            .Concat(clients).Append(new("probe.cs", """
                using System.Threading;
                using System.Threading.Tasks;
                using Sunder.Sdk.Rpc;
                using Sunder.Generated.Packages.Example_Dispatch_Services;
                using Sunder.Generated.Packages.Example_Dispatch_Internal;
                public static class DispatchProbe
                {
                    public static object Create(ISunderRpcClient rpc) => new CExample_Dispatch_BuilderClient(rpc);
                    public static Task<int> Start(object state, CancellationToken token) => ((IBuilderService)state).StartAsync(token).AsTask();
                    public static ValueTask Dispose(object state) => ((System.IAsyncDisposable)state).DisposeAsync();
                }
                """)), "DispatchProbe");
        try
        {
            var descriptor = Assert.Single(prepass.Compilation.Contracts).Descriptor;
            var provider = new SunderRpcProviderSnapshot("example.dispatch", "1.0.0", "example.dispatch.builder", descriptor.ContractId,
                descriptor.Version, descriptor.Sha256, descriptor.AbiSha256, Guid.NewGuid(), 2, 3, new("rpc1_builder"), 4, SunderRpcProviderState.Active);
            var rpc = new DispatchHandoffRpc(provider);
            var state = probe.GetMethod("Create")!.CreateDelegate<Func<ISunderRpcClient, object>>()(rpc);
            var dispose = probe.GetMethod("Dispose")!.CreateDelegate<Func<object, ValueTask>>();
            try
            {
                var start = probe.GetMethod("Start")!.CreateDelegate<Func<object, CancellationToken, Task<int>>>();
                var error = await Assert.ThrowsAsync<SunderRpcException>(() => start(state, default));
                Assert.Equal(1, rpc.Invocations);
                Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(error));
                Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, error.Error.DispatchState);
                Assert.Equal(SunderRpcRetrySafety.Unsafe, error.Error.RetrySafety);
            }
            finally { await dispose(state); }
        }
        finally { load.Unload(); }
    }

    [Fact]
    public void OnlyTheNewPredicateMemberInfersTheExistingObservationCapability()
    {
        var diagnostics = new List<string>();
        var catalog = SdkCapabilityCatalog.Create([typeof(SunderRpcException).Assembly.Location], diagnostics);
        var type = catalog.ResolveType("Sunder.Sdk", "Sunder.Sdk.Rpc", nameof(SunderRpcException));
        var member = catalog.ResolveMember("Sunder.Sdk", "Sunder.Sdk.Rpc", nameof(SunderRpcException), nameof(SunderRpcException.HasTrustedNoDispatchEvidence));
        Assert.Empty(diagnostics);
        Assert.True(member.IsResolved);
        Assert.Contains(SunderSdkCapabilities.WorkerExecutionObservationsV1, member.DeclaredCapabilities);
        Assert.DoesNotContain(SunderSdkCapabilities.WorkerExecutionObservationsV1, type.DeclaredCapabilities);
    }

    // No SDK friend access or synthetic Host-authentication marker: this owns the
    // actual emitter regression. Genuine JSON-origin authority is tested in App.
    private sealed class DispatchHandoffRpc(SunderRpcProviderSnapshot provider) : ISunderRpcClient
    {
        private readonly Channel<SunderRpcCatalogEvent> events = Channel.CreateUnbounded<SunderRpcCatalogEvent>();
        internal int Invocations;
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
            => Invocations == 0 ? ValueTask.FromResult<SunderRpcProviderSnapshot?>(provider)
                : ValueTask.FromException<SunderRpcProviderSnapshot?>(new SunderRpcException(new(SunderRpcErrorKind.Unavailable,
                    "rpc.metadata.not-dispatched", "Metadata rejected before its own dispatch.", SunderRpcDispatchState.NotDispatched, SunderRpcRetrySafety.Safe)));
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SunderRpcCatalogSnapshot(4, 1, [provider]));
        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
            => events.Reader.ReadAllAsync(cancellationToken);
        public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ISunderRpcCallScope>(new Scope(this));
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        { Assert.Equal("start", methodId); Invocations++; return ValueTask.FromResult(JsonSerializer.SerializeToElement(1)); }
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        private sealed class Scope(DispatchHandoffRpc owner) : ISunderRpcCallScope
        {
            public DateTimeOffset DeadlineUtc => DateTimeOffset.UtcNow.AddMinutes(1);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default) => owner.GetProviderAsync(endpoint, cancellationToken);
            public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default) => owner.DiscoverAsync(contractId, cancellationToken);
            public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default) => owner.WatchAsync(afterRevision, afterSequence, cancellationToken);
            public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
                SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => owner.InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
            public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
                SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string filePath, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
