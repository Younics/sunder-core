using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageWorkerExecutionObservationTests
{
    [Fact]
    public async Task CaptureRequiresPublishedOwnerAndRevocationClosesBothOperations()
    {
        var client = new ProbeClient();
        var publication = new AppPackageGenerationPublication();
        var service = new AppPackageWorkerExecutionObservations(client, publication);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync(Entry()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ObserveAsync(Reference));
        Assert.Equal(0, client.Discoveries);
        publication.Publish();
        Assert.Equal(Reference, await service.CaptureAsync(Entry()));
        Assert.Equal(Provider, client.Captured);
        publication.Revoke();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.CaptureAsync(Entry()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ObserveAsync(Reference));
        Assert.Equal(1, client.Captures);
        Assert.Equal(0, client.Observations);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("version")]
    [InlineData("provider")]
    [InlineData("contract")]
    [InlineData("contract-version")]
    [InlineData("descriptor")]
    [InlineData("abi")]
    [InlineData("activation")]
    [InlineData("epoch")]
    [InlineData("generation")]
    [InlineData("inactive")]
    public async Task CaptureCannotFollowReplacementOfAnyExactProviderField(string field)
    {
        var current = field switch
        {
            "package" => Provider with { PackageId = "other.package" },
            "version" => Provider with { PackageVersion = "1.0.1" },
            "provider" => Provider with { ProviderId = "other.provider" },
            "contract" => Provider with { ContractId = "other.contract" },
            "contract-version" => Provider with { ContractVersion = "1.0.1" },
            "descriptor" => Provider with { DescriptorSha256 = new('c', 64) },
            "abi" => Provider with { AbiSha256 = new('d', 64) },
            "activation" => Provider with { ActivationId = Guid.NewGuid() },
            "epoch" => Provider with { ActivationEpoch = 12 },
            "generation" => Provider with { SessionGeneration = 12 },
            _ => Provider with { State = SunderRpcProviderState.Inactive },
        };
        var client = new ProbeClient { Providers = [current] };
        var publication = Published();
        var error = await Assert.ThrowsAsync<SunderRpcException>(() =>
            new AppPackageWorkerExecutionObservations(client, publication).CaptureAsync(Entry()));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, error.Error.Kind);
        Assert.Equal(0, client.Captures);
    }

    [Fact]
    public async Task HistoricalObservationDoesNotRediscoverOrRequireAnActiveOriginalProvider()
    {
        var client = new ProbeClient { Providers = [] };
        var service = new AppPackageWorkerExecutionObservations(client, Published());
        var result = await service.ObserveAsync(Reference);
        Assert.Equal(Reference, result.Reference);
        Assert.Equal(PackageWorkerExecutionState.CleanupConfirmed, result.State);
        Assert.Equal(Reference, client.Observed);
        Assert.Equal(0, client.Discoveries);
        Assert.Equal(0, client.Captures);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task LateSuccessfulResponseCannotCrossCallerCancellationOrRevocation(bool capture, bool callerStop)
    {
        var entered = Signal();
        var resume = Signal();
        var client = new ProbeClient { BeforeOperation = async _ => { entered.SetResult(); await resume.Task; } };
        var publication = Published();
        var service = new AppPackageWorkerExecutionObservations(client, publication);
        using var caller = new CancellationTokenSource();
        var operation = capture ? (Task)service.CaptureAsync(Entry(), caller.Token) : service.ObserveAsync(Reference, caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (callerStop) caller.Cancel(); else publication.Revoke();
            resume.SetResult();
            if (callerStop)
            {
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
                Assert.Equal(caller.Token, error.CancellationToken);
            }
            else await Assert.ThrowsAsync<ObjectDisposedException>(() => operation);
            Assert.Equal(1, client.Captures + client.Observations);
        }
        finally { resume.TrySetResult(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RevocationDoesNotConvertForeignCancellationIntoSuccessfulStop(bool capture)
    {
        using var foreign = new CancellationTokenSource();
        foreign.Cancel();
        var failure = new OperationCanceledException("Unrelated cancellation.", foreign.Token);
        var publication = Published();
        var client = new ProbeClient { BeforeOperation = _ => { publication.Revoke(); throw failure; } };
        var service = new AppPackageWorkerExecutionObservations(client, publication);
        var operation = capture ? (Task)service.CaptureAsync(Entry()) : service.ObserveAsync(Reference);
        Assert.Same(failure, await Record.ExceptionAsync(() => operation));
    }

    [Fact]
    public async Task ObservationRejectsAReferenceMismatchInsteadOfAcknowledgingReplacement()
    {
        var client = new ProbeClient { Observation = _ => new(new(Guid.NewGuid(), "replacement"), PackageWorkerExecutionState.CleanupConfirmed) };
        var error = await Assert.ThrowsAsync<SunderRpcException>(() =>
            new AppPackageWorkerExecutionObservations(client, Published()).ObserveAsync(Reference));
        Assert.Equal(SunderRpcErrorKind.Protocol, error.Error.Kind);
    }

    [Fact]
    public void PackageCannotReplaceTheReservedObservationService()
    {
        var publication = Published();
        var replacement = new AppPackageWorkerExecutionObservations(new ProbeClient(), publication);
        var factory = new AppPackageServiceProviderFactory(null, null, null, null!, publication);
        // This assembly is also the ShellLifecycle package fixture. An on-disk
        // second module would correctly invalidate every metadata-loader fixture.
        var module = DispatchProxy.Create<ISunderAppPackageModule, ReplacingModuleProxy>();
        ((ReplacingModuleProxy)(object)module).Replacement = replacement;
        var error = Assert.Throws<InvalidOperationException>(() => factory.Create(null!, null!, module, new ProbeClient()));
        Assert.Contains(typeof(IPackageWorkerExecutionObservations).FullName!, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManagedCallRejectsLateSuccessAfterHandoffAndOnlyExplicitNextObservationUsesSuccessor(bool capture)
    {
        var entered = Signal();
        var resume = Signal();
        var first = new ProbeClient { BeforeOperation = async _ => { entered.SetResult(); await resume.Task; } };
        var successor = new ProbeClient();
        await using var client = new AppPackageManagedRpcClient(first, (_, _) => Task.FromResult<ISunderRpcClient>(successor), Published());
        var operation = capture ? (Task)client.CaptureWorkerExecutionAsync(Provider).AsTask()
            : client.ObserveWorkerExecutionAsync(Reference).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var replacement = await client.PrepareAsync(RuntimeContractTestData.Snapshot("test.package", PackageSourceKind.Dev, "test"), default);
            replacement.Commit();
            resume.SetResult();
            Assert.Equal("rpc.caller-retired", (await Assert.ThrowsAsync<SunderRpcException>(() => operation)).Error.Code);
            Assert.Equal(0, successor.Captures + successor.Observations);
            Assert.Equal(Reference, (await client.ObserveWorkerExecutionAsync(Reference)).Reference);
            Assert.Equal(1, successor.Observations);
        }
        finally { resume.TrySetResult(); }
    }

    [Fact]
    public async Task ManagedCapturePreservesUncertainFailureWithoutReplayingOnCommittedSuccessor()
    {
        var entered = Signal();
        var resume = Signal();
        var failure = new IOException("Capture response lost.");
        var first = new ProbeClient { BeforeOperation = async _ => { entered.SetResult(); await resume.Task; throw failure; } };
        var next = new ProbeClient();
        await using var client = new AppPackageManagedRpcClient(first, (_, _) => Task.FromResult<ISunderRpcClient>(next), Published());
        var operation = client.CaptureWorkerExecutionAsync(Provider).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var replacement = await client.PrepareAsync(RuntimeContractTestData.Snapshot("test.package", PackageSourceKind.Dev, "test"), default);
            replacement.Commit();
            resume.SetResult();
            Assert.Same(failure, await Record.ExceptionAsync(() => operation));
            Assert.Equal(1, first.Captures);
            Assert.Equal(0, next.Captures);
        }
        finally { resume.TrySetResult(); }
    }

    [Fact]
    public async Task RevokedManagedObservationCannotBorrowEvenAnExistingDisposalAuthority()
    {
        var raw = new ProbeClient();
        var publication = Published();
        await using var client = new AppPackageManagedRpcClient(raw, (_, _) => throw new InvalidOperationException("No reopen."), publication);
        publication.Revoke();
        using var cleanup = client.BeginCleanup();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CaptureWorkerExecutionAsync(Provider).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ObserveWorkerExecutionAsync(Reference).AsTask());
        Assert.Equal(0, raw.Captures + raw.Observations);
    }

    private static readonly PackageWorkerExecutionReference Reference = new(Guid.NewGuid(), "original-worker-execution");
    private static readonly SunderRpcProviderSnapshot Provider = new("test.package", "1.0.0", "test.provider", "test.contract", "1.0.0",
        new('a', 64), new('b', 64), Guid.NewGuid(), 2, 3, new("exact-provider-endpoint"), 4, SunderRpcProviderState.Active);
    private static ServiceProvider<object> Entry() => new(Provider, new object());
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static AppPackageGenerationPublication Published() { var value = new AppPackageGenerationPublication(); value.Publish(); return value; }

    public class ReplacingModuleProxy : DispatchProxy
    {
        internal IPackageWorkerExecutionObservations Replacement = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(ISunderAppPackageModule.ConfigureAppServices))
                throw new NotSupportedException("Reserved service replacement must fail before contributions are registered.");
            ((IServiceCollection)args![0]!).AddSingleton(Replacement);
            return null;
        }
    }

    private sealed class ProbeClient : ISunderRpcClient, IAppWorkerExecutionObservationClient
    {
        internal IReadOnlyList<SunderRpcProviderSnapshot> Providers = [Provider];
        internal Func<CancellationToken, Task>? BeforeOperation;
        internal Func<PackageWorkerExecutionReference, PackageWorkerExecutionObservation> Observation = reference =>
            new(reference, PackageWorkerExecutionState.CleanupConfirmed) { ConfirmedAtUtc = DateTimeOffset.UtcNow };
        internal int Discoveries, Captures, Observations;
        internal SunderRpcProviderSnapshot? Captured;
        internal PackageWorkerExecutionReference? Observed;
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
        { Discoveries++; return ValueTask.FromResult(new SunderRpcCatalogSnapshot(1, 1, Providers)); }
        public async ValueTask<PackageWorkerExecutionReference> CaptureWorkerExecutionAsync(SunderRpcProviderSnapshot provider, CancellationToken cancellationToken = default)
        { Captures++; Captured = provider; if (BeforeOperation is not null) await BeforeOperation(cancellationToken); return Reference; }
        public async ValueTask<PackageWorkerExecutionObservation> ObserveWorkerExecutionAsync(PackageWorkerExecutionReference reference, CancellationToken cancellationToken = default)
        { Observations++; Observed = reference; if (BeforeOperation is not null) await BeforeOperation(cancellationToken); return Observation(reference); }
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
