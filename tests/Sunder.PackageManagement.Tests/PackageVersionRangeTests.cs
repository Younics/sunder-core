using Sunder.PackageManagement;
using Xunit;

namespace Sunder.PackageManagement.Tests;

public sealed class PackageVersionRangeTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3", ">=1.0.0")]
    [InlineData("1.2.3", ">=1.0.0 <2.0.0")]
    [InlineData("1.2.3-beta.1", ">=1.2.0")]
    public void IsSatisfiedBy_WhenRangeMatches_ReturnsTrue(string version, string range)
    {
        Assert.True(PackageVersionRange.IsSatisfiedBy(version, range));
    }

    [Theory]
    [InlineData("1.2.3", "2.0.0")]
    [InlineData("1.2.3", ">=2.0.0")]
    [InlineData("1.2.3", ">=1.0.0 <1.2.0")]
    [InlineData("not-a-version", ">=1.0.0")]
    [InlineData("1.2.3", "")]
    public void IsSatisfiedBy_WhenRangeDoesNotMatch_ReturnsFalse(string version, string range)
    {
        Assert.False(PackageVersionRange.IsSatisfiedBy(version, range));
    }
}
