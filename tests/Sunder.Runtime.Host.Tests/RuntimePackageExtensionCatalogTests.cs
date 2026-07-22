using Microsoft.Extensions.DependencyInjection;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimePackageExtensionCatalogTests
{
    private static readonly PackageExtensionPoint<ITestContribution> TestPoint = new("test:contribution");

    [Fact]
    public void Add_ReturnsRegisteredContributionsInRegistrationOrder()
    {
        var catalog = new RuntimePackageExtensionCatalog();
        var first = new TestContribution("first");
        var second = new TestContribution("second");

        catalog.Add("first.package", TestPoint, first);
        catalog.Add("second.package", TestPoint, second);

        Assert.Equal(["first", "second"], catalog.GetExtensions(TestPoint).Select(contribution => contribution.Name).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Package.Owner")]
    public void Add_RejectsMissingOrNoncanonicalOwnershipBeforeMutation(string packageId)
    {
        var catalog = new RuntimePackageExtensionCatalog();

        Assert.Throws<ArgumentException>(() => catalog.Add(packageId, TestPoint, new TestContribution("invalid")));

        Assert.Empty(catalog.GetExtensions(TestPoint));
    }

    [Fact]
    public void RemovePackage_RemovesOnlyMatchingPackageContributions()
    {
        var catalog = new RuntimePackageExtensionCatalog();
        catalog.Add("first.package", TestPoint, new TestContribution("first"));
        catalog.Add("second.package", TestPoint, new TestContribution("second"));

        catalog.RemovePackage("FIRST.PACKAGE");

        var contribution = Assert.Single(catalog.GetExtensions(TestPoint));
        Assert.Equal("second", contribution.Name);
    }

    [Fact]
    public void RegisterExtension_AddsContributionToRuntimeCatalog()
    {
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var catalog = new RuntimePackageExtensionCatalog();
        var registry = new RuntimePackageContributionRegistry(serviceProvider, catalog, "test.package");
        var contribution = new TestContribution("registered");

        registry.RegisterExtension(TestPoint, contribution);

        Assert.True(registry.HasRegisteredExtensions);
        Assert.Same(contribution, Assert.Single(catalog.GetExtensions(TestPoint)));
    }

    [Fact]
    public void Batch_PublishesOneDefensivelyCopiedRevision()
    {
        var catalog = new RuntimePackageExtensionCatalog();
        var revisions = new List<PackageExtensionCatalogChangedEventArgs>();
        catalog.Changed += (_, change) => revisions.Add(change);

        using (var batch = catalog.BeginBatch(PackageExtensionCatalogChangeReason.PackageActivated))
        {
            catalog.Add("test.package", TestPoint, new TestContribution("first"));
            catalog.Add("test.package", TestPoint, new TestContribution("second"));
            batch.Commit();
        }

        var revision = Assert.Single(revisions);
        Assert.Equal(1, revision.Revision);
        Assert.Equal(PackageExtensionCatalogChangeReason.PackageActivated, revision.Reason);
        Assert.Equal(2, revision.Changes.Count);
        Assert.All(revision.Changes, change =>
        {
            Assert.Equal("test.package", change.PackageId);
            Assert.Equal(TestPoint.Id, change.ExtensionPointId);
            Assert.Equal(PackageExtensionChangeKind.Added, change.Kind);
            Assert.Equal(typeof(TestContribution), change.ContributionType);
        });
        Assert.True(revision.IncludesExtensionPoint("TEST:CONTRIBUTION"));
        var mutableView = Assert.IsAssignableFrom<IList<PackageExtensionChange>>(revision.Changes);
        Assert.Throws<NotSupportedException>(() => mutableView[0] = revision.Changes[1]);
    }

    [Fact]
    public void Changed_IsolatesSubscriberFailureAndNotifiesRemainingSubscribers()
    {
        var catalog = new RuntimePackageExtensionCatalog();
        PackageExtensionCatalogChangedEventArgs? observed = null;
        catalog.Changed += (_, _) => throw new InvalidOperationException("Subscriber failed.");
        catalog.Changed += (_, change) => observed = change;

        var exception = Record.Exception(() =>
            catalog.Add("test.package", TestPoint, new TestContribution("registered")));

        Assert.Null(exception);
        Assert.NotNull(observed);
        Assert.Equal(1, observed.Revision);
        Assert.Single(catalog.GetExtensions(TestPoint));
    }

    private interface ITestContribution
    {
        string Name { get; }
    }

    private sealed record TestContribution(string Name) : ITestContribution;
}
