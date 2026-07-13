using Sunder.Sdk.Packaging;
using Xunit;

namespace Sunder.Package.Format.Tests;

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

    [Theory]
    [InlineData("1.2.3 || 2.0.0")]
    [InlineData("^1.2.3")]
    [InlineData("~1.2.3")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3 - 2.0.0")]
    [InlineData(">= 1.2.3")]
    [InlineData(">=1.2.3, <2.0.0")]
    [InlineData("1.2.3 <2.0.0")]
    [InlineData(">=1.2.3\t<2.0.0")]
    [InlineData(" >=1.2.3")]
    [InlineData(">=1.2.3 ")]
    [InlineData("=>1.2.3")]
    [InlineData(">=01.2.3")]
    public void TryParse_RejectsMalformedOrAmbiguousGrammar(string value)
        => Assert.False(PackageVersionRange.TryParse(value, out _));

    [Fact]
    public void Parse_FormatsConjunctionDeterministically()
        => Assert.Equal(">=1.0.0 <2.0.0", PackageVersionRange.Parse(">=1.0.0  <2.0.0").ToString());
}
