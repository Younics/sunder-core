using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpImportPrepassTests
{
    [Fact]
    public async Task GeneratedCatalogBorrowedSelectionProbesFreshPermissionAfterEndpointUnavailable()
    {
        using var probe = CompileProbe(SunderRpcCSharpImportPrepass.Prepare(_lock, _root));
        var selected = Provider("1.2.3");
        var scopes = new List<RpcStub>();
        var rpc = new RpcStub
        {
            LiveCatalog = true, Current = selected, Providers = [selected],
            ScopeFactory = _ =>
            {
                var scope = new RpcStub { Current = selected };
                scopes.Add(scope);
                return ValueTask.FromResult<ISunderRpcCallScope>(scope);
            },
        };
        var catalog = probe.CreateNaturalCatalog(rpc);
        await using var lifetime = (IAsyncDisposable)catalog;
        var old = Assert.IsAssignableFrom<object>(await probe.BorrowNatural(catalog, default));
        var oldService = old.GetType().GetProperty("Service")!.GetValue(old);
        var oldRetirement = (CancellationToken)old.GetType().GetProperty("RetirementToken")!.GetValue(old)!;
        Assert.Equal(1, scopes.Sum(scope => scope.InvokeCount));

        // Match the real Memory.Semantic transport: discovery succeeds throughout;
        // only endpoint GetProviderAsync changes, and there is no recovery-loop sleep.
        var unavailable = new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "test.lookup-failed", "Test provider lookup failed."));
        rpc.LookupFailure = unavailable;
        Assert.Null(await probe.BorrowNatural(catalog, default));
        Assert.True(oldRetirement.IsCancellationRequested);
        var lookups = rpc.LookupCount;
        var denied = new SunderRpcException(new(SunderRpcErrorKind.PermissionDenied, "test.lookup-failed", "Test provider lookup failed."));
        rpc.LookupFailure = denied;
        Assert.Same(denied, await Assert.ThrowsAsync<SunderRpcException>(() => probe.BorrowNatural(catalog, default)));
        Assert.True(rpc.LookupCount > lookups);
        Assert.Equal(1, scopes.Sum(scope => scope.InvokeCount));

        rpc.LookupFailure = unavailable;
        for (var attempt = 0; attempt < 8; attempt++)
            Assert.Null(await probe.BorrowNatural(catalog, default));
        Assert.Single(rpc.WatchCursors);

        rpc.LookupFailure = null;
        var fresh = Assert.IsAssignableFrom<object>(await probe.BorrowNatural(catalog, default));
        Assert.NotSame(old, fresh);
        Assert.NotSame(oldService, fresh.GetType().GetProperty("Service")!.GetValue(fresh));
        Assert.True(oldRetirement.IsCancellationRequested);
        Assert.Equal(2, scopes.Sum(scope => scope.InvokeCount));
        Assert.All(scopes, scope => Assert.True(scope.Disposed));
        Assert.Single(rpc.WatchCursors);
    }
}
