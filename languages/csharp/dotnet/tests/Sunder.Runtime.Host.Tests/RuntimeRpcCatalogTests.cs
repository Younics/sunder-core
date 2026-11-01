using Sunder.Runtime.Host.Services;
using Xunit;
using static Sunder.Runtime.Host.Tests.RuntimeRpcBrokerFixture;
using static Sunder.Runtime.Host.Tests.RuntimeRpcTestPackages;

namespace Sunder.Runtime.Host.Tests;

[Collection("Runtime RPC broker")]
public sealed class RuntimeRpcCatalogTests
{
    [Fact]
    public async Task Catalog_ReportsReplayGapAndEvictsSlowSubscriber()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler());
        var subscription = fixture.Catalog.Subscribe(
            fixture.Catalog.Revision,
            fixture.Catalog.GetSnapshot().Sequence);
        await using (subscription)
        {
            for (var generation = 2; generation < 80; generation++)
            {
                fixture.Catalog.DeactivatePackage("provider.package", activation.Provider.ActivationId, faulted: false);
                fixture.Catalog.ActivateSession(activation.Session, generation);
            }
            await Assert.ThrowsAsync<SlowRuntimeStreamConsumerException>(() =>
                DrainAsync(subscription.Reader));
        }

        var page = fixture.Catalog.GetEventPage(afterRevision: 1, afterSequence: 1);
        Assert.True(page.ResetRequired);
        Assert.Empty(page.Events);
    }

    [Fact]
    public async Task Catalog_DuplicateProviderIdsFailAtomically()
    {
        var descriptor = Contract();
        var first = CreatePackage("first.package", descriptor, new TestHandler(), usesContract: false);
        var second = CreatePackage("second.package", descriptor, new TestHandler(), usesContract: false);
        var session = CreateSession(first, second);
        using var catalog = new RuntimeRpcCatalog();

        var exception = Assert.Throws<InvalidOperationException>(() => catalog.ActivateSession(session, 1));
        Assert.Contains("more than one", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, catalog.Revision);
        Assert.Empty(catalog.GetSnapshot().Providers);
        await session.DisposeAsync();
    }
}
