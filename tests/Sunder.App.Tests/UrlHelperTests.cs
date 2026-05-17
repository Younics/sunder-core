using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class UrlHelperTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5275", "http://127.0.0.1:5275/")]
    [InlineData("https://runtime.example.test/base/", "https://runtime.example.test/base/")]
    public void RuntimeUrlHelper_TryParse_NormalizesHttpUrls(string value, string expected)
    {
        var parsed = RuntimeUrlHelper.TryParse(value, out var uri);

        Assert.True(parsed);
        Assert.Equal(new Uri(expected), uri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("file:///tmp/runtime")]
    public void RuntimeUrlHelper_TryParse_RejectsInvalidUrls(string? value)
    {
        var parsed = RuntimeUrlHelper.TryParse(value, out var uri);

        Assert.False(parsed);
        Assert.Null(uri);
    }

    [Theory]
    [InlineData(" http://registry.example.test ", "http://registry.example.test/")]
    [InlineData("https://registry.example.test/api", "https://registry.example.test/api/")]
    [InlineData("https://registry.example.test/api/", "https://registry.example.test/api/")]
    public void RegistryUrlHelper_TryParse_NormalizesHttpUrls(string value, string expected)
    {
        var parsed = RegistryUrlHelper.TryParse(value, out var uri);

        Assert.True(parsed);
        Assert.Equal(new Uri(expected), uri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://registry.example.test")]
    public void RegistryUrlHelper_TryParse_RejectsInvalidUrls(string? value)
    {
        var parsed = RegistryUrlHelper.TryParse(value, out var uri);

        Assert.False(parsed);
        Assert.Null(uri);
    }
}
