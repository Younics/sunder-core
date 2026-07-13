using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Packaging;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class PackagingPrimitiveTests
{
    [Fact]
    public void PackageId_EnforcesCanonicalGrammarAndLength()
    {
        Assert.True(PackageId.TryParse(new string('a', PackageId.MaximumLength), out _));
        Assert.False(PackageId.TryParse(new string('a', PackageId.MaximumLength + 1), out _));
        Assert.False(PackageId.TryParse("Package.Owner", out _));
        Assert.False(PackageId.TryParse("package-owner", out _));
    }

    [Fact]
    public void SemanticVersion_EnforcesPracticalLengthLimit()
    {
        Assert.True(SemanticVersion.TryParse("1.2.3-preview.1+linux-x64", out _));
        Assert.False(SemanticVersion.TryParse("1.0.0+" + new string('a', SemanticVersion.MaximumLength), out _));
    }

    [Fact]
    public void PackageVersionRange_EnforcesPracticalLengthLimit()
        => Assert.False(PackageVersionRange.TryParse(new string('1', PackageVersionRange.MaximumLength + 1), out _));

    [Fact]
    public void ExtensionContribution_RequiresCanonicalOwner()
    {
        Assert.Throws<ArgumentException>(() => new PackageExtensionContribution<object>(string.Empty, new object()));
        Assert.Throws<ArgumentException>(() => new PackageExtensionContribution<object>("Package.Owner", new object()));

        var contribution = new PackageExtensionContribution<string>("package.owner", "value");

        Assert.Equal("package.owner", contribution.PackageId);
        Assert.Equal("value", contribution.Contribution);
    }

    [Fact]
    public void PackagingPrimitives_AreOwnedOnlyBySdk()
    {
        var formatAssembly = typeof(SunderPackageArchiveInspector).Assembly;

        Assert.Same(typeof(SunderPackageAttribute).Assembly, typeof(PackageId).Assembly);
        Assert.Same(typeof(SunderPackageAttribute).Assembly, typeof(SemanticVersion).Assembly);
        Assert.Same(typeof(SunderPackageAttribute).Assembly, typeof(PackageVersionRange).Assembly);
        Assert.Null(formatAssembly.GetType("Sunder.Package.Format.PackageId"));
        Assert.Null(formatAssembly.GetType("Sunder.Package.Format.SemanticVersion"));
        Assert.Null(formatAssembly.GetType("Sunder.Package.Format.PackageVersionRange"));
    }

    [Fact]
    public void RoleLocalWorkspace_IsAHostOwnedNonDisposableView()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(IPackageRoleLocalWorkspace)));
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IPackageRoleLocalWorkspace)));
    }
}
