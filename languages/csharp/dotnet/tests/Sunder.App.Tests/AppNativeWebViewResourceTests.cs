using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppNativeWebViewResourceTests
{
    [Theory]
    [InlineData("about:blank", true, false)]
    [InlineData("about:blank", false, true)]
    [InlineData("about:blank#other", true, true)]
    [InlineData("about:srcdoc", true, true)]
    [InlineData("https://example.test/", true, true)]
    [InlineData("http://127.0.0.1:12345/package/index.html", true, true)]
    public void RetiredResourcesStopExceptTheHostsExactNeutralNavigation(string request, bool retiringDocument, bool shouldStop)
    {
        var stops = 0;
        var forwarded = 0;
        AppNativeWebView.DispatchResourceRequest(new(request), "GET", disposed: true, retiringDocument,
            () => stops++, _ => forwarded++);
        Assert.Equal(shouldStop ? 1 : 0, stops);
        Assert.Equal(0, forwarded); // The retiring document never reacquires bridge authority.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveResourcesStillPassThroughPackagePolicyAndHonorItsDenial(bool denied)
    {
        var uri = new Uri("http://127.0.0.1:12345/package/index.html");
        var stops = 0;
        AppWebResourceRequestedEventArgs? forwarded = null;
        AppNativeWebView.DispatchResourceRequest(uri, "HEAD", disposed: false, retiringDocument: false,
            () => stops++, resource => { forwarded = resource; resource.Cancel = denied; });
        Assert.NotNull(forwarded);
        Assert.Same(uri, forwarded.Uri);
        Assert.Equal("HEAD", forwarded.Method);
        Assert.Equal(denied ? 1 : 0, stops);
    }
}
