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
        var registry = new CollectingPackageContributionRegistry(serviceProvider, catalog, "test.package");
        var contribution = new TestContribution("registered");

        registry.RegisterExtension(TestPoint, contribution);

        Assert.True(registry.HasRegisteredExtensions);
        Assert.Same(contribution, Assert.Single(catalog.GetExtensions(TestPoint)));
    }

    private interface ITestContribution
    {
        string Name { get; }
    }

    private sealed record TestContribution(string Name) : ITestContribution;
}
