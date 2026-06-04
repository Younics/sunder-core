using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RegistryBrowserAuthFlowTests
{
    [Fact]
    public void BuildAuthorizeUri_EncodesRedirectUriOnce()
    {
        var callbackUri = new Uri("http://127.0.0.1:64307/callback");

        var authorizeUri = RegistryBrowserAuthFlow.BuildAuthorizeUri(
            new Uri("http://localhost:5288/"),
            callbackUri,
            "state value",
            "challenge_value");

        var query = ParseQuery(authorizeUri.Query);
        Assert.Equal("http://localhost:5288/cli/authorize", authorizeUri.GetLeftPart(UriPartial.Path));
        Assert.Equal(callbackUri.ToString(), query["redirect_uri"]);
        Assert.Equal("state value", query["state"]);
        Assert.Equal("challenge_value", query["code_challenge"]);
        Assert.Equal("Sunder App", query["display_name"]);
        Assert.DoesNotContain("%25", authorizeUri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildCallbackPage_UsesCliResultPageDesignWithAppCopy()
    {
        var html = RegistryBrowserAuthFlow.BuildCallbackPage();

        Assert.Contains("https://fonts.googleapis.com", html, StringComparison.Ordinal);
        Assert.Contains("IBM+Plex+Sans", html, StringComparison.Ordinal);
        Assert.Contains("radial-gradient(circle at 20% 10%", html, StringComparison.Ordinal);
        Assert.Contains("class=\"boot-shell\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"boot-mark\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"boot-title\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"boot-subtitle\"", html, StringComparison.Ordinal);
        Assert.Contains("Sunder Registry authorized", html, StringComparison.Ordinal);
        Assert.Contains("You can close this window and return to Sunder.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>", html, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = segment.Split('=', 2);
            if (parts.Length == 2)
            {
                result[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1].Replace('+', ' '));
            }
        }

        return result;
    }
}
