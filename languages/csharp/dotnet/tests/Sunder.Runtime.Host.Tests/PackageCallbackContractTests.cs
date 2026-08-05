using Sunder.Sdk.Callbacks;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageCallbackContractTests
{
    [Fact]
    public void StartContext_TakesImmutableParameterSnapshot()
    {
        var source = new Dictionary<string, string> { ["serverId"] = "one" };
        var context = new PackageCallbackStartContext(
            "session",
            new Uri("http://localhost/callbacks/session"),
            "mcp.oauth.v1",
            source);

        source["serverId"] = "two";

        Assert.Equal("one", context.Parameters["serverId"]);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)context.Parameters).Add("other", "value"));
    }

    [Fact]
    public void StartContext_RejectsParametersOutsideV1Bounds()
    {
        var values = Enumerable.Range(0, PackageCallbackParameters.MaximumCount + 1)
            .ToDictionary(index => $"key{index}", index => index.ToString());

        Assert.Throws<ArgumentException>(() => new PackageCallbackStartContext(
            "session",
            new Uri("http://localhost/callbacks/session"),
            parameters: values));
    }
}
