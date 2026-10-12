using System.Collections;
using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpImportPrepassTests
{
    [Fact]
    public async Task NaturalCatalogEntriesAreLazyExactBoundAndRetiredWithTheirOwner()
    {
        using var probe = CompileProbe(SunderRpcCSharpImportPrepass.Prepare(_lock, _root));
        var extension = Provider("9.0.0") with { PackageId = "example.extension" };
        var inactive = extension with { ProviderId = "example.inactive", Endpoint = new("inactive"), State = SunderRpcProviderState.Inactive };
        var scope = new RpcStub { Current = extension };
        var rpc = new RpcStub
        {
            LiveCatalog = true, Current = extension,
            Providers = [extension, inactive, extension with { ProviderId = "example.other.abi", Endpoint = new("other-abi"), AbiSha256 = new string('f', 64) }],
            ScopeFactory = _ => ValueTask.FromResult<ISunderRpcCallScope>(scope),
        };
        var catalog = probe.CreateNaturalCatalog(rpc);
        Assert.Equal(0, rpc.DiscoverCount); Assert.Equal(0, rpc.ScopeCount);
        var snapshot = await probe.ListNatural(catalog, default);
        var providers = ((IEnumerable)snapshot.GetType().GetProperty("Providers")!.GetValue(snapshot)!).Cast<object>().ToArray();
        Assert.Equal(2, providers.Length);
        Assert.Equal(7L, snapshot.GetType().GetProperty("Revision")!.GetValue(snapshot));
        Assert.Equal(10L, snapshot.GetType().GetProperty("Sequence")!.GetValue(snapshot));
        Assert.Equal(false, snapshot.GetType().GetProperty("ResetRequired")!.GetValue(snapshot));
        Assert.Equal(0, rpc.ScopeCount);

        await probe.CallNatural(providers[0], default);
        Assert.Equal(extension.Endpoint, scope.RequestedEndpoint);
        Assert.Equal(1, scope.InvokeCount); Assert.Equal(0, scope.DiscoverCount); Assert.True(scope.Disposed);
        scope = new RpcStub { Current = extension with { SessionGeneration = extension.SessionGeneration + 1 } };
        var scopedFailure = await Assert.ThrowsAsync<SunderRpcException>(() => probe.CallNatural(providers[0], default));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, scopedFailure.Error.Kind);
        Assert.Equal(0, scope.InvokeCount); Assert.True(scope.Disposed);
        Assert.Equal("rpc.stale-endpoint", (await Assert.ThrowsAsync<SunderRpcException>(() => probe.CallNatural(providers[1], default))).Error.Code);

        scope = new RpcStub { Current = extension with { SessionGeneration = extension.SessionGeneration + 1 } };
        rpc.Current = scope.Current;
        Assert.Equal("rpc.stale-endpoint", (await Assert.ThrowsAsync<SunderRpcException>(() => probe.CallNatural(providers[0], default))).Error.Code);
        Assert.Equal(0, scope.InvokeCount); Assert.False(scope.Disposed); // Receiver preflight precedes scope creation.
        rpc.Current = extension;
        var failure = new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "rpc.stale-endpoint", "retired after dispatch"));
        scope = new RpcStub { Current = extension, Failure = failure };
        Assert.Same(failure, await Assert.ThrowsAsync<SunderRpcException>(() => probe.CallNatural(providers[0], default)));
        Assert.Equal(1, scope.InvokeCount); Assert.True(scope.Disposed);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope = new RpcStub { Current = extension, Invoke = async token =>
        {
            entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonSerializer.SerializeToElement(new { });
        } };
        var blocked = probe.CallNatural(providers[0], default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var retired = ((IAsyncDisposable)catalog).DisposeAsync().AsTask();
        var canceledByRetirement = await Assert.ThrowsAsync<SunderRpcException>(() => blocked);
        Assert.Equal("rpc.binding.retired", canceledByRetirement.Error.Code);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, canceledByRetirement.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, canceledByRetirement.Error.RetrySafety);
        await retired.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(scope.Disposed);
        var scopesBefore = rpc.ScopeCount;
        var ended = await Assert.ThrowsAsync<SunderRpcException>(() => probe.CallNatural(providers[0], default));
        Assert.Equal(SunderRpcErrorKind.Unavailable, ended.Error.Kind);
        Assert.Equal("rpc.binding.lifetime-disposed", ended.Error.Code);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => probe.ListNatural(catalog, default));
        Assert.Equal(scopesBefore, rpc.ScopeCount);
    }

    [Fact]
    public async Task NaturalCatalogWatchPreservesBothCursorPartsLifecycleAndReset()
    {
        using var probe = CompileProbe(SunderRpcCSharpImportPrepass.Prepare(_lock, _root));
        var provider = Provider("1.2.3");
        var rpc = new RpcStub { LiveCatalog = true, Providers = [provider], Current = provider, Events =
        [
            new(32, 73, SunderRpcCatalogEventKind.Added, provider),
            new(33, 74, SunderRpcCatalogEventKind.Removed, provider with { State = SunderRpcProviderState.Inactive }),
            new(34, 75, SunderRpcCatalogEventKind.Added, provider with { ContractVersion = "99.0.0" }),
            new(35, 76, SunderRpcCatalogEventKind.ResetRequired, null),
        ] };
        var catalog = probe.CreateNaturalCatalog(rpc);
        await using var lifetime = (IAsyncDisposable)catalog;
        var events = await probe.WatchNatural(catalog, 31, 72, default);
        Assert.Contains((31L, 72L), rpc.WatchCursors); // The independent shared observer has its own real snapshot cursor.
        Assert.Equal(new[] { "Added", "Removed", "ResetRequired" }, events.Select(value => value.GetType().GetProperty("Kind")!.GetValue(value)!.ToString()));
        Assert.Null(events[^1].GetType().GetProperty("Provider")!.GetValue(events[^1]));
        Assert.Equal(0, rpc.ScopeCount);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.WatchNatural(catalog, 31, 72, canceled.Token));
    }
}
