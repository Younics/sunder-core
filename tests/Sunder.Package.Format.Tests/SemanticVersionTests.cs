using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class SemanticVersionTests
{
    [Fact]
    public void CompareTo_OrdersSemVerPrereleasesBySpecification()
    {
        var ordered = new[]
        {
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0",
        }.Select(SemanticVersion.Parse).ToArray();

        for (var index = 1; index < ordered.Length; index++)
        {
            Assert.True(ordered[index - 1] < ordered[index]);
        }
    }

    [Fact]
    public void BuildMetadata_DoesNotAffectPrecedenceButRemainsPartOfValueEquality()
    {
        var left = SemanticVersion.Parse("1.2.3-beta.1+linux.arm64");
        var right = SemanticVersion.Parse("1.2.3-beta.1+windows.x64");

        Assert.Equal(0, left.CompareTo(right));
        Assert.True(left.HasSamePrecedence(right));
        Assert.NotEqual(left, right);
        Assert.True(PackageVersionRange.IsSatisfiedBy(left.ToString(), "=1.2.3-beta.1+other"));
    }

    [Theory]
    [InlineData("0.0.0")]
    [InlineData("999999999999999999999999.2.3")]
    [InlineData("1.2.3-alpha-1.2+build.009")]
    public void Parse_FormatsDeterministically(string value)
        => Assert.Equal(value, SemanticVersion.Parse(value).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-alpha..1")]
    [InlineData("1.2.3+build..1")]
    [InlineData("1.2.3+build+other")]
    [InlineData("v1.2.3")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.3_foo")]
    public void TryParse_RejectsMalformedVersions(string value)
        => Assert.False(SemanticVersion.TryParse(value, out _));

    [Fact]
    public void NextMajor_SupportsComponentsBeyondSystemVersionLimits()
        => Assert.Equal(
            "1000000000000000000000000.0.0",
            SemanticVersion.Parse("999999999999999999999999.4.5-beta").NextMajor().ToString());
}
