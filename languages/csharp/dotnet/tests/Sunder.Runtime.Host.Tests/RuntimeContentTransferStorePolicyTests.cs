using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeContentTransferStorePolicyTests
{
    [Fact]
    public async Task PerReferenceBytes_AcceptsExactLimitAndRejectsOneByteOver()
    {
        await WithStoreAsync(
            Policy(maxReferenceBytes: 3),
            async (store, _endpoint) =>
            {
                var authority = new RuntimeRpcContentAuthority("per-reference");
                _ = await RegisterAsync(store, [1, 2, 3], "package.a", authority);

                var exception = await Assert.ThrowsAsync<SunderRpcException>(() =>
                    RegisterAsync(store, [1, 2, 3, 4], "package.a", authority));

                Assert.Equal(SunderRpcErrorKind.ResourceExhausted, exception.Error.Kind);
            });
    }

    [Fact]
    public async Task AggregateCallReservation_IsAtomicAndReleasedOnDiscard()
    {
        await WithStoreAsync(
            Policy(maxReferenceBytes: 4, callBytes: 4),
            async (store, endpoint) =>
            {
                var authority = new RuntimeRpcContentAuthority("call");
                var first = await RegisterAsync(store, [1, 2], "package.a", authority);
                _ = await RegisterAsync(store, [3, 4], "package.a", authority);

                var exception = await Assert.ThrowsAsync<SunderRpcException>(() =>
                    RegisterAsync(store, [5], "package.a", authority));
                Assert.Equal(SunderRpcErrorKind.ResourceExhausted, exception.Error.Kind);

                Discard(store, first, "package.a", authority, endpoint);
                _ = await RegisterAsync(store, [5], "package.a", authority);
            });
    }

    [Fact]
    public async Task AggregatePackageAndRuntimeReservations_EnforceExactTotals()
    {
        await WithStoreAsync(
            Policy(
                maxReferenceBytes: 6,
                callBytes: 20,
                packageBytes: 4,
                runtimeBytes: 6),
            async (store, _endpoint) =>
            {
                _ = await RegisterAsync(
                    store,
                    [1, 2, 3, 4],
                    "package.a",
                    new RuntimeRpcContentAuthority("a1"));
                var packageFailure = await Assert.ThrowsAsync<SunderRpcException>(() =>
                    RegisterAsync(
                        store,
                        [5],
                        "package.a",
                        new RuntimeRpcContentAuthority("a2")));

                _ = await RegisterAsync(
                    store,
                    [5, 6],
                    "package.b",
                    new RuntimeRpcContentAuthority("b1"));
                var runtimeFailure = await Assert.ThrowsAsync<SunderRpcException>(() =>
                    RegisterAsync(
                        store,
                        [7],
                        "package.c",
                        new RuntimeRpcContentAuthority("c1")));

                Assert.Equal(SunderRpcErrorKind.ResourceExhausted, packageFailure.Error.Kind);
                Assert.Equal(SunderRpcErrorKind.ResourceExhausted, runtimeFailure.Error.Kind);
            });
    }

    [Fact]
    public async Task FailedStreamingGrowth_RollsBackReservationAndPartialFile()
    {
        await WithStoreAsync(
            Policy(maxReferenceBytes: 8, callBytes: 4),
            async (store, _endpoint) =>
            {
                var authority = new RuntimeRpcContentAuthority("rollback");
                var failure = await Assert.ThrowsAsync<SunderRpcException>(() =>
                    RegisterAsync(store, [1, 2, 3, 4, 5], "package.a", authority));
                Assert.Equal(SunderRpcErrorKind.ResourceExhausted, failure.Error.Kind);

                _ = await RegisterAsync(store, [1, 2, 3, 4], "package.a", authority);
            });
    }

    [Fact]
    public async Task ActiveReferenceCount_IsBoundedAndReleasedByRevocation()
    {
        await WithStoreAsync(
            Policy(maxReferenceBytes: 4, callReferences: 1),
            async (store, _endpoint) =>
            {
                var authority = new RuntimeRpcContentAuthority("count");
                _ = await RegisterAsync(store, [], "package.a", authority);
                var failure = await Assert.ThrowsAsync<SunderRpcException>(() =>
                    RegisterAsync(store, [], "package.a", authority));
                Assert.Equal(SunderRpcErrorKind.ResourceExhausted, failure.Error.Kind);

                store.DiscardRpcContentAuthority(authority);
                _ = await RegisterAsync(
                    store,
                    [],
                    "package.a",
                    new RuntimeRpcContentAuthority("replacement"));
            });
    }

    private static RuntimeTransportPolicyOptions Policy(
        long maxReferenceBytes,
        long callBytes = 100,
        long packageBytes = 100,
        long runtimeBytes = 100,
        int callReferences = 10)
        => new()
        {
            MaxRpcContentBytes = maxReferenceBytes,
            MaxRpcContentBytesPerCall = callBytes,
            MaxRpcContentBytesPerPackage = packageBytes,
            MaxRpcContentBytesPerRuntime = runtimeBytes,
            MaxRpcContentReferencesPerCall = callReferences,
            MaxRpcContentReferencesPerPackage = 100,
            MaxRpcContentReferencesPerRuntime = 100,
        };

    private static async Task<SunderRpcContentReference> RegisterAsync(
        RuntimeContentTransferStore store,
        byte[] bytes,
        string ownerPackageId,
        RuntimeRpcContentAuthority authority)
    {
        await using var stream = new MemoryStream(bytes);
        return await store.RegisterRpcContentAsync(
            stream,
            expectedLength: null,
            "application/octet-stream",
            "content.bin",
            ownerPackageId,
            "audience.package",
            generation: 1,
            Endpoint,
            DateTimeOffset.UtcNow.AddMinutes(5),
            SunderRpcContentRepeatability.Repeatable,
            maximumUses: 2,
            CancellationToken.None,
            authority);
    }

    private static void Discard(
        RuntimeContentTransferStore store,
        SunderRpcContentReference reference,
        string ownerPackageId,
        RuntimeRpcContentAuthority authority,
        RuntimeRpcContentEndpoint endpoint)
        => store.DiscardRpcContent(
            reference,
            ownerPackageId,
            "audience.package",
            generation: 1,
            endpoint,
            authority);

    private static async Task WithStoreAsync(
        RuntimeTransportPolicyOptions policy,
        Func<RuntimeContentTransferStore, RuntimeRpcContentEndpoint, Task> action)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sunder-runtime-content-policy-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new RuntimeContentTransferStore(
                new RuntimePackagePaths(root),
                policy);
            await action(store, Endpoint);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static RuntimeRpcContentEndpoint Endpoint { get; } = new(
        "rpc1_content",
        Guid.Parse("11111111-1111-1111-1111-111111111111"));
}
