using Sunder.App.Services;
using Sunder.Runtime.Client;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppWebPendingRequestRegistryTests
{
    [Fact]
    public void Reserve_EnforcesExactCapacityAndBoundsRequestIdHistory()
    {
        var gate = new object();
        var registry = new AppWebPendingRequestRegistry(gate);
        var allocations = 0;
        AppWebPendingRequest[] detached;

        lock (gate)
        {
            for (var index = 0;
                 index < RuntimeClientResourceLimits.MaximumPendingBrowserRequests;
                 index++)
            {
                var outcome = registry.TryReserve(
                    $"b_{index}",
                    () =>
                    {
                        allocations++;
                        return CreatePendingRequest();
                    },
                    out var pending);

                Assert.Equal(AppWebPendingRequestRegistry.ReservationOutcome.Reserved, outcome);
                Assert.NotNull(pending);
            }

            for (var index = RuntimeClientResourceLimits.MaximumPendingBrowserRequests;
                 index <= AppWebPendingRequestRegistry.MaximumRememberedRequestIds;
                 index++)
            {
                var outcome = registry.TryReserve(
                    $"b_{index}",
                    static () => throw new InvalidOperationException("A rejected request must not be allocated."),
                    out var pending);

                Assert.Equal(AppWebPendingRequestRegistry.ReservationOutcome.CapacityReached, outcome);
                Assert.Null(pending);
            }

            Assert.Equal(RuntimeClientResourceLimits.MaximumPendingBrowserRequests, allocations);
            Assert.Equal(RuntimeClientResourceLimits.MaximumPendingBrowserRequests, registry.Count);
            Assert.Equal(
                AppWebPendingRequestRegistry.MaximumRememberedRequestIds,
                registry.RememberedRequestIdCount);

            Assert.Equal(
                AppWebPendingRequestRegistry.ReservationOutcome.RequestIdReused,
                registry.TryReserve(
                    $"b_{AppWebPendingRequestRegistry.MaximumRememberedRequestIds}",
                    static () => throw new InvalidOperationException("A duplicate request must not be allocated."),
                    out var rememberedDuplicate));
            Assert.Null(rememberedDuplicate);

            detached = registry.DetachAll();
            Assert.Equal(0, registry.Count);
            Assert.Equal(0, registry.RememberedRequestIdCount);
        }

        AppWebPendingRequestRegistry.CancelAndDispose(detached);
        Assert.All(detached, static request => Assert.True(request.IsDisposed));
    }

    [Fact]
    public void Completion_DuplicateAndLateResponsesCannotRemoveReplacement()
    {
        var gate = new object();
        var registry = new AppWebPendingRequestRegistry(gate);
        AppWebPendingRequest original;
        AppWebPendingRequest replacement;

        lock (gate)
        {
            Assert.Equal(
                AppWebPendingRequestRegistry.ReservationOutcome.Reserved,
                registry.TryReserve("b_same", CreatePendingRequest, out var reservedOriginal));
            original = Assert.IsType<AppWebPendingRequest>(reservedOriginal);

            Assert.Equal(
                AppWebPendingRequestRegistry.ReservationOutcome.RequestIdReused,
                registry.TryReserve(
                    "b_same",
                    static () => throw new InvalidOperationException("A duplicate request must not be allocated."),
                    out var duplicate));
            Assert.Null(duplicate);

            Assert.True(registry.TryComplete("b_same", original));
            Assert.False(registry.TryComplete("b_same", original));
            Assert.Equal(0, registry.Count);

            Assert.Empty(registry.DetachAll());
            Assert.Equal(
                AppWebPendingRequestRegistry.ReservationOutcome.Reserved,
                registry.TryReserve("b_same", () => CreatePendingRequest(documentGeneration: 2), out var reservedReplacement));
            replacement = Assert.IsType<AppWebPendingRequest>(reservedReplacement);

            Assert.False(registry.TryComplete("b_same", original));
            Assert.True(registry.IsCurrent("b_same", replacement));
            Assert.Equal(1, registry.Count);
            Assert.True(registry.TryComplete("b_same", replacement));
            Assert.Equal(0, registry.Count);
        }

        original.Dispose();
        replacement.Dispose();
        Assert.True(original.IsDisposed);
        Assert.True(replacement.IsDisposed);
    }

    [Fact]
    public void NavigationReset_CancelsDetachedRequestsAndAllowsEventualCleanup()
    {
        var gate = new object();
        var registry = new AppWebPendingRequestRegistry(gate);
        AppWebPendingRequest first;
        AppWebPendingRequest second;
        AppWebPendingRequest[] detached;

        lock (gate)
        {
            Assert.Equal(
                AppWebPendingRequestRegistry.ReservationOutcome.Reserved,
                registry.TryReserve("b_1", CreatePendingRequest, out var reservedFirst));
            Assert.Equal(
                AppWebPendingRequestRegistry.ReservationOutcome.Reserved,
                registry.TryReserve("b_2", CreatePendingRequest, out var reservedSecond));
            first = Assert.IsType<AppWebPendingRequest>(reservedFirst);
            second = Assert.IsType<AppWebPendingRequest>(reservedSecond);

            detached = registry.DetachAll();
            Assert.Equal(0, registry.Count);
            Assert.Equal(0, registry.RememberedRequestIdCount);
        }

        AppWebPendingRequestRegistry.Cancel(detached);
        Assert.All(detached, static request =>
        {
            Assert.True(request.Token.IsCancellationRequested);
            Assert.False(request.TimedOut);
            Assert.False(request.IsCompleted);
            Assert.False(request.IsDisposed);
        });

        lock (gate)
        {
            Assert.False(registry.TryComplete("b_1", first));
            Assert.False(registry.TryComplete("b_2", second));
            Assert.Equal(
                AppWebPendingRequestRegistry.ReservationOutcome.Reserved,
                registry.TryReserve("b_1", () => CreatePendingRequest(documentGeneration: 2), out _));
            detached = registry.DetachAll();
        }

        first.Dispose();
        second.Dispose();
        AppWebPendingRequestRegistry.CancelAndDispose(detached);
        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
    }

    [Fact]
    public void Disposal_CancelsAndDisposesEveryRequestWithoutLeaks()
    {
        var gate = new object();
        var registry = new AppWebPendingRequestRegistry(gate);
        AppWebPendingRequest[] detached;
        CancellationToken[] tokens;

        lock (gate)
        {
            Assert.Equal(
                AppWebPendingRequestRegistry.ReservationOutcome.Reserved,
                registry.TryReserve("b_1", CreatePendingRequest, out _));
            Assert.Equal(
                AppWebPendingRequestRegistry.ReservationOutcome.Reserved,
                registry.TryReserve("b_2", CreatePendingRequest, out _));

            detached = registry.DetachAll();
            tokens = detached.Select(static request => request.Token).ToArray();
            Assert.Equal(0, registry.Count);
            Assert.Equal(0, registry.RememberedRequestIdCount);
        }

        AppWebPendingRequestRegistry.CancelAndDispose(detached);
        AppWebPendingRequestRegistry.CancelAndDispose(detached);

        Assert.All(tokens, static token => Assert.True(token.IsCancellationRequested));
        Assert.All(detached, static request => Assert.True(request.IsDisposed));
    }

    private static AppWebPendingRequest CreatePendingRequest()
        => CreatePendingRequest(documentGeneration: 1);

    private static AppWebPendingRequest CreatePendingRequest(long documentGeneration)
    {
        var deadline = new CancellationTokenSource();
        try
        {
            return new AppWebPendingRequest(
                documentGeneration,
                CancellationToken.None,
                CancellationToken.None,
                deadline);
        }
        catch
        {
            deadline.Dispose();
            throw;
        }
    }
}
