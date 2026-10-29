using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class DotNetWorkerProfileTests
{
    [Theory]
    [InlineData("net10.0", ">=10.0.0 <11.0.0")]
    [InlineData("net11.0", ">=11.0.0 <12.0.0")]
    public void RecognizedProfilesKeepTheirOwnRuntimeRange(string framework, string expected)
    {
        Assert.True(DotNetWorkerProfile.TryGetRuntimeRange(framework, out var range));
        Assert.Equal(expected, range);
    }

    [Theory]
    [InlineData("net9.0")]
    [InlineData("net010.0")]
    [InlineData("net10.1")]
    [InlineData("net10.0-windows")]
    [InlineData("net.0")]
    [InlineData(null)]
    public void InvalidProfilesAreRejected(string? framework)
        => Assert.False(DotNetWorkerProfile.TryGetRuntimeRange(framework, out _));

    [Fact]
    public void RecognitionDoesNotGrantCurrentAuthoringSupport()
    {
        Assert.True(DotNetWorkerProfile.IsSupported("net10.0"));
        Assert.False(DotNetWorkerProfile.IsSupported("net11.0"));
    }
}
