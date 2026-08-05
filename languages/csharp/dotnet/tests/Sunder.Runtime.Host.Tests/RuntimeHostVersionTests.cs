using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeHostVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0+dc8532fb5a8ebc0743056383720c51edae671e8a", "1.0.0")]
    [InlineData("1.0.0-beta.1+dc8532fb5a8ebc0743056383720c51edae671e8a", "1.0.0-beta.1")]
    public void StripBuildMetadata_RemovesSourceRevisionSuffix(string value, string expected)
    {
        var result = RuntimeHostVersion.StripBuildMetadata(value);

        Assert.Equal(expected, result);
    }
}
