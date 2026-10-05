using Sunder.Sdk.Packaging;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class PackageDependencyRangeValidationTests
{
    [Theory]
    [InlineData(">=2.0.0 <1.0.0")]
    [InlineData("<1.0.0 >=2.0.0")]
    [InlineData(">=1.0.0 <2.0.0 >=3.0.0")]
    [InlineData("<=4.0.0 >3.0.0 <3.0.0")]
    [InlineData(">1.0.0 <1.0.0")]
    [InlineData(">1.0.0 <=1.0.0")]
    [InlineData(">=1.0.0 <1.0.0")]
    [InlineData(">1.0.0 >=1.0.0 <=1.0.0")]
    [InlineData(">=1.0.0 >1.0.0 <=1.0.0")]
    [InlineData(">=1.0.0 <1.0.0 <=1.0.0")]
    [InlineData(">=1.0.0 <=1.0.0 <1.0.0")]
    [InlineData("=1.0.0 =2.0.0")]
    [InlineData("=2.0.0 =1.0.0")]
    [InlineData("=1.0.0 >1.0.0")]
    [InlineData("=1.0.0 <1.0.0")]
    [InlineData("=1.0.0 >=2.0.0 <=3.0.0")]
    [InlineData("=2.0.0 >1.0.0 <2.0.0")]
    [InlineData(">1.0.0+first <=1.0.0+second")]
    [InlineData(">=1.0.0+first <1.0.0+second")]
    [InlineData(">=1.0.0-beta <1.0.0-alpha")]
    [InlineData(">=1.0.0-alpha.10 <=1.0.0-alpha.9")]
    [InlineData(">1.0.0 <1.0.1-0")]
    [InlineData(">1.0.0-alpha <1.0.0-alpha.0")]
    [InlineData(">1.0.0-alpha.1 <1.0.0-alpha.1.0")]
    [InlineData(">1.0.0-0 <1.0.0-0.0")]
    [InlineData(">0.0.0-0 <0.0.0-0.0")]
    [InlineData("<0.0.0-0")]
    [InlineData("<0.0.0-0+build")]
    [InlineData(">1.0.99999999999999999999 <1.0.100000000000000000000-0")]
    [InlineData(">=100000000000000000000.0.0 <99999999999999999999.0.0")]
    public void Validate_RejectsEmptyOrdinaryDependencyRanges(string range)
    {
        Assert.True(PackageVersionRange.TryParse(range, out _));
        Assert.Equal($"Dependency 'example.dependency' versionRange '{range}' permits no semantic version.",
            Assert.Single(Validate(range)));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+reviewed", "1.2.3+selected")]
    [InlineData("=1.2.3", "1.2.3")]
    [InlineData(">=1.0.0 <=1.0.0", "1.0.0")]
    [InlineData("<=1.0.0 >=1.0.0", "1.0.0")]
    [InlineData(">=1.0.0 <2.0.0 =1.2.3", "1.2.3")]
    [InlineData("=1.2.3 >=1.0.0 <2.0.0", "1.2.3")]
    [InlineData("=1.2.3 =1.2.3", "1.2.3")]
    [InlineData("=1.2.3+first =1.2.3+second", "1.2.3+third")]
    [InlineData(">=1.0.0+z <=1.0.0+a", "1.0.0")]
    [InlineData(">=1.0.0+a <=1.0.0+z", "1.0.0+other")]
    [InlineData(">1.0.0 <1.0.1", "1.0.1-0")]
    [InlineData(">1.0.0 <=1.0.1-0", "1.0.1-0")]
    [InlineData(">=1.0.0 <1.0.1-0", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha")]
    [InlineData("=1.0.0-alpha+first =1.0.0-alpha+second", "1.0.0-alpha")]
    [InlineData(">1.0.0-alpha <=1.0.0-alpha.0", "1.0.0-alpha.0")]
    [InlineData(">=1.0.0-alpha <1.0.0-alpha.0", "1.0.0-alpha")]
    [InlineData(">1.0.0-alpha <1.0.0-beta", "1.0.0-alpha.0")]
    [InlineData(">1.0.0-alpha.9 <1.0.0-alpha.10", "1.0.0-alpha.9.0")]
    [InlineData(">1.0.0-9 <1.0.0-A", "1.0.0-9.0")]
    [InlineData(">1.0.0-Z <1.0.0-a", "1.0.0-Z.0")]
    [InlineData("<=0.0.0-0", "0.0.0-0")]
    [InlineData("<0.0.0", "0.0.0-0")]
    [InlineData(">=0.0.0-0 <0.0.0-0.0", "0.0.0-0")]
    [InlineData(">1.2.3", "1.2.4-0")]
    [InlineData(">=1.0.0 <4.0.0", "2.0.0")]
    [InlineData(">1.0.99999999999999999999 <=1.0.100000000000000000000-0", "1.0.100000000000000000000-0")]
    [InlineData(">=99999999999999999999.0.0 <100000000000000000000.0.0", "99999999999999999999.0.0")]
    public void Validate_AcceptsRangesWithWitnessesUsingExistingPrecedenceSemantics(string range, string witness)
    {
        Assert.True(PackageVersionRange.IsSatisfiedBy(witness, range));
        Assert.Empty(Validate(range));
    }

    [Theory]
    [InlineData("required")]
    [InlineData("optional")]
    public void Validate_RequiresNonemptyRangesRegardlessOfPresence(string presence)
    {
        Assert.Empty(Validate(">=1.0.0 <=1.0.0", presence));
        Assert.Equal("Dependency 'example.dependency' versionRange '>=2.0.0 <1.0.0' permits no semantic version.",
            Assert.Single(Validate(">=2.0.0 <1.0.0", presence)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("^1.0.0")]
    [InlineData("1.0.0 <2.0.0")]
    [InlineData(">=1.0.0  <2.0.0")]
    [InlineData(">=1.0.0\t<2.0.0")]
    [InlineData(" >=1.0.0 <2.0.0")]
    [InlineData(">=1.0.0 <2.0.0 ")]
    public void Validate_PreservesSyntaxAndCanonicalWhitespaceRejection(string? range)
    {
        Assert.Equal($"Dependency 'example.dependency' has unsupported or noncanonical versionRange '{range}'.",
            Assert.Single(Validate(range)));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1-0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.0")]
    public void Validate_DropsBuildMetadataBeforeConstructingSuccessor(string lower, string successor)
    {
        lower += "+" + new string('b', SemanticVersion.MaximumLength - lower.Length - 1);
        Assert.Equal(SemanticVersion.MaximumLength, lower.Length);
        var emptyRange = $">{lower} <{successor}";
        Assert.True(PackageVersionRange.TryParse(emptyRange, out _));
        Assert.Equal($"Dependency 'example.dependency' versionRange '{emptyRange}' permits no semantic version.",
            Assert.Single(Validate(emptyRange)));
        Assert.Empty(Validate($">{lower} <={successor}"));
    }

    [Fact]
    public void Validate_ChecksPrereleaseAdjacencyWhenSuccessorExactlyFitsVersionLengthLimit()
    {
        var lower = "1.0.0-" + new string('a', SemanticVersion.MaximumLength - "1.0.0-".Length - 2);
        var successor = lower + ".0";
        Assert.Equal(SemanticVersion.MaximumLength, successor.Length);
        Assert.True(SemanticVersion.TryParse(successor, out _));
        var emptyRange = $">{lower} <{successor}";
        Assert.Equal($"Dependency 'example.dependency' versionRange '{emptyRange}' permits no semantic version.",
            Assert.Single(Validate(emptyRange)));
        Assert.Empty(Validate($">{lower} <={successor}"));
    }

    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    public void Validate_DoesNotRejectInLengthPrereleasesWhenAppendedSuccessorIsTooLong(int length)
    {
        var prefix = "1.0.0-" + new string('a', length - "1.0.0-".Length - 1);
        var lower = prefix + "a";
        var witness = prefix + "b";
        var range = $">{lower} <{prefix}c";
        Assert.Equal(length, lower.Length);
        Assert.True(SemanticVersion.TryParse(lower, out _));
        Assert.False(SemanticVersion.TryParse(lower + ".0", out _));
        Assert.True(PackageVersionRange.IsSatisfiedBy(witness, range));
        Assert.Empty(Validate(range));
        Assert.Equal($"Dependency 'example.dependency' versionRange '>{lower} <={lower}' permits no semantic version.",
            Assert.Single(Validate($">{lower} <={lower}")));
        AssertAdjacent(lower, length == SemanticVersion.MaximumLength ? prefix + "b" : lower + "-");
    }

    [Fact]
    public void Validate_DoesNotRejectHigherCoreWhenNextPatchSuccessorIsTooLong()
    {
        var lower = "1.0." + new string('9', SemanticVersion.MaximumLength - "1.0.".Length);
        var range = $">{lower} <1.1.0";
        Assert.True(SemanticVersion.TryParse(lower, out _));
        Assert.True(PackageVersionRange.IsSatisfiedBy("1.1.0-0", range));
        Assert.Empty(Validate(range));
        AssertAdjacent(lower, "1.1.0-0");
    }

    [Theory]
    [InlineData("a", 3, "a.0")]
    [InlineData("0", 3, "0.0")]
    [InlineData("a", 2, "a-")]
    [InlineData("z", 2, "z-")]
    [InlineData("-", 2, "--")]
    [InlineData("0", 1, "1")]
    [InlineData("8", 1, "9")]
    [InlineData("9", 2, "10")]
    [InlineData("99", 3, "100")]
    [InlineData("9", 1, "-")]
    [InlineData("99", 2, "-")]
    [InlineData("999999999999999999999999", 24, "-")]
    [InlineData("999999999999999999999999", 25, "1000000000000000000000000")]
    [InlineData("-", 1, "A")]
    [InlineData("--", 2, "-0")]
    [InlineData("-9", 2, "-A")]
    [InlineData("-Z", 2, "-a")]
    [InlineData("-z", 2, "0-")]
    [InlineData("0-", 2, "0A")]
    [InlineData("09-", 3, "09A")]
    [InlineData("0-z", 3, "00-")]
    [InlineData("9-", 2, "9A")]
    [InlineData("A", 1, "B")]
    [InlineData("Z", 1, "a")]
    [InlineData("a", 1, "b")]
    [InlineData("az", 2, "b")]
    [InlineData("azz", 3, "b")]
    [InlineData("z", 1, null)]
    [InlineData("zzz", 3, null)]
    [InlineData("a.z", 3, "a-")]
    [InlineData("0.z", 3, "1")]
    [InlineData("9.z", 3, "10")]
    [InlineData("a.9", 3, "a.-")]
    [InlineData("a.-", 3, "a.A")]
    [InlineData("a.-z", 4, "a.0-")]
    [InlineData("a.zz", 4, "a-")]
    [InlineData("a.0.z", 5, "a.1")]
    [InlineData("a.z.z", 5, "a.z-")]
    [InlineData("z.z", 3, "z-")]
    public void Validate_UsesMinimumLengthBoundedPrereleaseSuccessor(
        string prerelease, int prereleaseBudget, string? successorPrerelease)
    {
        var core = new string('1', SemanticVersion.MaximumLength - prereleaseBudget - ".0.0-".Length) + ".0.0";
        Assert.Equal(prereleaseBudget, SemanticVersion.MaximumLength - core.Length - 1);
        AssertAdjacent($"{core}-{prerelease}", successorPrerelease is null ? core : $"{core}-{successorPrerelease}");
    }

    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    public void Validate_UsesStableNextPatchWhenMinimumPrereleaseDoesNotFit(int length)
    {
        var prefix = "1.0." + new string('1', length - "1.0.".Length - 1);
        AssertAdjacent(prefix + "1", prefix + "2");
    }

    [Fact]
    public void Validate_HandlesPatchDigitCarryUpToVersionLengthLimit()
    {
        var patch = new string('9', SemanticVersion.MaximumLength - "1.0.".Length - 1);
        AssertAdjacent("1.0." + patch, "1.0.1" + new string('0', patch.Length));
    }

    [Fact]
    public void Validate_CarriesToStableNextMinorWhenPrereleaseDoesNotFit()
    {
        var major = new string('9', SemanticVersion.MaximumLength - ".0.9".Length);
        AssertAdjacent($"{major}.0.9", $"{major}.1.0");
    }

    [Fact]
    public void Validate_CarriesMinorDigitsToNextMajorPrerelease()
    {
        var minor = new string('9', SemanticVersion.MaximumLength - "1..9".Length);
        AssertAdjacent($"1.{minor}.9", "2.0.0-0");
    }

    [Fact]
    public void Validate_CarriesMajorDigitsWithinVersionLengthLimit()
    {
        var digits = SemanticVersion.MaximumLength - "1.9.9".Length;
        AssertAdjacent("1" + new string('9', digits) + ".9.9", "2" + new string('0', digits) + ".0.0");
    }

    [Fact]
    public void Validate_RejectsRangeAboveMaximumRepresentableVersion()
    {
        var maximum = new string('9', SemanticVersion.MaximumLength - ".9.9".Length) + ".9.9";
        Assert.Equal(SemanticVersion.MaximumLength, maximum.Length);
        Assert.True(SemanticVersion.TryParse(maximum, out _));
        var range = $">{maximum}";
        Assert.Equal($"Dependency 'example.dependency' versionRange '{range}' permits no semantic version.",
            Assert.Single(Validate(range)));
        Assert.Empty(Validate($">={maximum}"));
        Assert.Empty(Validate($"={maximum}"));
    }

    private static void AssertAdjacent(string lower, string successor)
    {
        Assert.True(SemanticVersion.TryParse(lower, out var lowerVersion));
        Assert.True(SemanticVersion.TryParse(successor, out var successorVersion));
        Assert.True(lowerVersion.ComparePrecedenceTo(successorVersion) < 0);
        var emptyRange = $">{lower} <{successor}";
        Assert.Equal($"Dependency 'example.dependency' versionRange '{emptyRange}' permits no semantic version.",
            Assert.Single(Validate(emptyRange)));
        Assert.Empty(Validate($">{lower} <={successor}"));
        Assert.Empty(Validate($">={lower} <{successor}"));
    }

    private static List<string> Validate(string? range, string presence = "required")
    {
        var errors = new List<string>();
        PackageDependencyValidator.Validate([new SunderPackageDependencyManifest
        {
            PackageId = "example.dependency", VersionRange = range, Presence = presence,
        }], errors);
        return errors;
    }
}
