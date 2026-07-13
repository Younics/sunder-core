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
}
