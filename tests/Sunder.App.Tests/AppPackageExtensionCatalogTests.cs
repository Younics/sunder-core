using Sunder.App.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageExtensionCatalogTests
{
    private static readonly PackageExtensionPoint<object> TestPoint = new("test:ownership");

    [Theory]
    [InlineData("")]
    [InlineData("Package.Owner")]
    public void Add_RejectsMissingOrNoncanonicalOwnershipBeforeMutation(string packageId)
    {
        var catalog = new AppPackageExtensionCatalog();

        Assert.Throws<ArgumentException>(() => catalog.Add(packageId, TestPoint, new object()));

        Assert.Empty(catalog.GetExtensions(TestPoint));
    }

    [Fact]
    public void SameExtensionPointId_WithDifferentContractTypes_KeepsBucketsIsolated()
    {
        var catalog = new AppPackageExtensionCatalog();
        var textPoint = new PackageExtensionPoint<string>("test:shared");
        var numberPoint = new PackageExtensionPoint<int>("test:shared");

        catalog.Add("test.package", textPoint, "value");
        catalog.Add("test.package", numberPoint, 42);

        Assert.Equal(["value"], catalog.GetExtensions(textPoint));
        Assert.Equal([42], catalog.GetExtensions(numberPoint));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" test:point")]
    [InlineData("test:\npoint")]
    public void Add_RejectsInvalidExtensionPointId(string extensionPointId)
    {
        var catalog = new AppPackageExtensionCatalog();
        var extensionPoint = new PackageExtensionPoint<object>(extensionPointId);

        Assert.Throws<ArgumentException>(() => catalog.Add("test.package", extensionPoint, new object()));
    }
}
