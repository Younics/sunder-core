using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Contracts;
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
        var owner = catalog.BeginOwnerActivation("test.package");
        var revisions = new List<PackageExtensionCatalogChangedEventArgs>();
        catalog.Changed += (_, change) => revisions.Add(change);

        using (var batch = catalog.BeginBatch(owner, PackageExtensionCatalogChangeReason.PackageActivated))
        {
            catalog.Add(owner, TestPoint, new TestContribution("first"));
            catalog.Add(owner, TestPoint, new TestContribution("second"));

            Assert.Empty(catalog.GetExtensions(TestPoint));
            Assert.Empty(catalog.GetExtensionReferences(TestPoint));
            Assert.Empty(revisions);

            batch.Commit();

            Assert.Equal(["first", "second"], catalog.GetExtensions(TestPoint).Select(static item => item.Name));
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
    public async Task Batch_RollbackDiscardsStagedContributionsWithoutRevision()
    {
        var catalog = new RuntimePackageExtensionCatalog();
        var owner = catalog.BeginOwnerActivation("test.package");
        var revisions = new List<PackageExtensionCatalogChangedEventArgs>();
        catalog.Changed += (_, change) => revisions.Add(change);

        using (catalog.BeginBatch(owner, PackageExtensionCatalogChangeReason.PackageActivated))
        {
            catalog.Add(owner, TestPoint, new TestContribution("staged"));
            Assert.Empty(catalog.GetExtensions(TestPoint));
        }

        Assert.Empty(catalog.GetExtensions(TestPoint));
        Assert.Empty(catalog.GetExtensionReferences(TestPoint));
        Assert.Empty(revisions);
        await catalog.BeginOwnerRetirement(owner).Completion;
        Assert.Empty(revisions);
    }

    [Fact]
    public async Task FailedActivationBeforeOwnerCreation_DoesNotRetireCurrentSameIdEpoch()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var catalog = new RuntimePackageExtensionCatalog();
        var owner = catalog.BeginOwnerActivation("test.package");
        catalog.Add(owner, TestPoint, new TestContribution("current"));
        var reference = Assert.Single(catalog.GetExtensionReferences(TestPoint));
        using var sharedAssemblies = new RuntimeSharedAssemblyRegistry([]);
        var source = new RuntimePackageSource("test.package", PackageSourceKind.Dev, root);
        var package = new PreparedRuntimePackage(
            root,
            source,
            root,
            root,
            "test.package",
            "1.0.0",
            PackageHostRoles.Runtime,
            new RuntimePackageActivationState(
                "test.package",
                "Test Package",
                "1.0.0",
                PackageHostRoles.Runtime,
                Icon: null),
            Path.Combine(root, "missing.dll"),
            Dependencies: []);
        var activator = new RuntimePackageActivator(NullLogger.Instance, new RuntimePackagePaths(root));

        try
        {
            var result = await activator.ActivateAsync(
                package,
                sharedAssemblies,
                catalog,
                new List<string>(),
                new List<string>(),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(reference.TryAcquire(out var lease));
            Assert.Equal("current", lease.Contribution.Name);
            lease.Dispose();
        }
        finally
        {
            await catalog.BeginOwnerRetirement(owner).Completion;
            Directory.Delete(root, recursive: true);
        }
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

    [Fact]
    public async Task OwnerRetirement_RejectsNewAcquisitionsAndDrainsExistingLease()
    {
        var catalog = new RuntimePackageExtensionCatalog();
        var owner = catalog.BeginOwnerActivation("test.package");
        catalog.Add(owner, TestPoint, new TestContribution("active"));
        var reference = Assert.Single(catalog.GetExtensionReferences(TestPoint));
        Assert.True(reference.TryAcquire(out var lease));

        var retirement = catalog.BeginOwnerRetirement(owner);

        Assert.True(lease.RetirementToken.IsCancellationRequested);
        Assert.False(reference.TryAcquire(out _));
        Assert.False(retirement.Completion.IsCompleted);

        lease.Dispose();
        lease.Dispose();
        await retirement.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task OwnerRetirement_DoesNotWaitForAnotherOwnersLease()
    {
        var catalog = new RuntimePackageExtensionCatalog();
        var firstOwner = catalog.BeginOwnerActivation("first.package");
        var secondOwner = catalog.BeginOwnerActivation("second.package");
        catalog.Add(firstOwner, TestPoint, new TestContribution("first"));
        catalog.Add(secondOwner, TestPoint, new TestContribution("second"));
        var references = catalog.GetExtensionReferences(TestPoint);
        Assert.True(references[0].TryAcquire(out var firstLease));
        Assert.True(references[1].TryAcquire(out var secondLease));

        var firstRetirement = catalog.BeginOwnerRetirement(firstOwner);
        firstLease.Dispose();

        await firstRetirement.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(secondLease.RetirementToken.IsCancellationRequested);

        var secondRetirement = catalog.BeginOwnerRetirement(secondOwner);
        secondLease.Dispose();
        await secondRetirement.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ReactivationWithSameIds_DoesNotReviveOldReference()
    {
        var catalog = new RuntimePackageExtensionCatalog();
        var firstOwner = catalog.BeginOwnerActivation("test.package");
        catalog.Add(firstOwner, TestPoint, new TestContribution("same"));
        var oldReference = Assert.Single(catalog.GetExtensionReferences(TestPoint));

        await catalog.BeginOwnerRetirement(firstOwner).Completion;
        var replacementOwner = catalog.BeginOwnerActivation("test.package");
        catalog.Add(replacementOwner, TestPoint, new TestContribution("same"));
        var replacementReference = Assert.Single(catalog.GetExtensionReferences(TestPoint));

        Assert.False(oldReference.TryAcquire(out _));
        Assert.True(replacementReference.TryAcquire(out var replacementLease));
        Assert.Equal("test.package", replacementLease.PackageId);
        Assert.Equal("same", replacementLease.Contribution.Name);
        replacementLease.Dispose();
        await catalog.BeginOwnerRetirement(replacementOwner).Completion;
    }

    [Fact]
    public void DisposedLease_RejectsContributionAndMetadataAccess()
    {
        var catalog = new RuntimePackageExtensionCatalog();
        catalog.Add("test.package", TestPoint, new TestContribution("active"));
        var reference = Assert.Single(catalog.GetExtensionReferences(TestPoint));
        Assert.True(reference.TryAcquire(out var lease));

        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => lease.PackageId);
        Assert.Throws<ObjectDisposedException>(() => lease.Contribution);
        Assert.Throws<ObjectDisposedException>(() => lease.RetirementToken);
    }

    private interface ITestContribution
    {
        string Name { get; }
    }

    private sealed record TestContribution(string Name) : ITestContribution;
}
