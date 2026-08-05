using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeListenUrlValidatorTests
{
    [Fact]
    public void ParseAndValidate_WhenUrlIsMissing_UsesIpv4LoopbackDefault()
    {
        var urls = RuntimeListenUrlValidator.ParseAndValidate(null, allowNonLoopback: false);

        Assert.Equal([RuntimeListenUrlValidator.DefaultListenUrl], urls);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5275")]
    [InlineData("http://[::1]:5275")]
    [InlineData("https://localhost:5275")]
    public void ParseAndValidate_WhenUrlIsLoopback_Accepts(string url)
    {
        var urls = RuntimeListenUrlValidator.ParseAndValidate(url, allowNonLoopback: false);

        Assert.Equal([url], urls);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5275")]
    [InlineData("http://*:5275")]
    [InlineData("http://runtime.example.test:5275")]
    public void ParseAndValidate_WhenUrlIsNotLoopback_Rejects(string url)
    {
        Assert.Throws<InvalidOperationException>(
            () => RuntimeListenUrlValidator.ParseAndValidate(url, allowNonLoopback: false));
    }

    [Fact]
    public void ParseAndValidate_WhenDevelopmentOverrideIsExplicit_AllowsNonLoopback()
    {
        const string url = "http://0.0.0.0:5275";

        var urls = RuntimeListenUrlValidator.ParseAndValidate(url, allowNonLoopback: true);

        Assert.Equal([url], urls);
    }

    [Fact]
    public void ParseAndValidate_WhenMultipleUrlsAreConfigured_RejectsAmbiguousDiscovery()
    {
        Assert.Throws<InvalidOperationException>(
            () => RuntimeListenUrlValidator.ParseAndValidate(
                "http://127.0.0.1:5275;http://[::1]:5275",
                allowNonLoopback: false));
    }
}
