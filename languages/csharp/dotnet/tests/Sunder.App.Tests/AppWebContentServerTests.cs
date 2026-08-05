using System.Net;
using System.Text;
using Sunder.App.Services;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppWebContentServerTests
{
    [Fact]
    public async Task ServesOnlyHashedPackageOriginWithStrictHeadersRoutesAndMethods()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "index.html"),
            "<!doctype html><html><head></head><body>secure app</body></html>",
            Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(root, "assets", "app.js"), "export const ready = true;", Encoding.UTF8);
        try
        {
            await using var server = await AppWebContentServer.StartAsync(root, Target(), CancellationToken.None);
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });

            using var route = await client.GetAsync(server.GetRouteUri("/workspace"));
            var html = await route.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, route.StatusCode);
            Assert.Contains($"<base href=\"{server.BasePath}\">", html, StringComparison.Ordinal);
            var contentSecurityPolicy = Header(route, "Content-Security-Policy");
            Assert.Contains("default-src 'none'", contentSecurityPolicy, StringComparison.Ordinal);
            Assert.Contains("frame-ancestors 'none'", contentSecurityPolicy, StringComparison.Ordinal);
            Assert.Equal("nosniff", Header(route, "X-Content-Type-Options"));
            Assert.Equal("no-store", route.Headers.CacheControl?.ToString());

            using var asset = await client.GetAsync(new Uri(server.BaseUri, "assets/app.js"));
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.Contains("immutable", asset.Headers.CacheControl?.ToString(), StringComparison.Ordinal);

            using var headRequest = new HttpRequestMessage(HttpMethod.Head, server.BaseUri);
            using var head = await client.SendAsync(headRequest);
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());

            using var post = await client.PostAsync(server.BaseUri, new StringContent("ignored"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);

            using var wrongCase = await client.GetAsync(new Uri(server.BaseUri, "Assets/app.js"));
            Assert.Equal(HttpStatusCode.NotFound, wrongCase.StatusCode);
            using var query = await client.GetAsync(new Uri(server.BaseUri + "?leak=1"));
            Assert.Equal(HttpStatusCode.NotFound, query.StatusCode);

            using var forgedHostRequest = new HttpRequestMessage(HttpMethod.Get, server.BaseUri);
            forgedHostRequest.Headers.Host = $"localhost:{server.Origin.Port}";
            using var forgedHost = await client.SendAsync(forgedHostRequest);
            Assert.Equal((HttpStatusCode)421, forgedHost.StatusCode);

            await File.WriteAllTextAsync(Path.Combine(root, "assets", "app.js"), "export const changed = true;", Encoding.UTF8);
            using var changed = await client.GetAsync(new Uri(server.BaseUri, "assets/app.js"));
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static SunderPackageTargetManifest Target()
        => new()
        {
            Role = SunderPackageFormat.AppHostRole,
            Rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            Kind = SunderPackageFormat.WebTargetKind,
            EntryPoint = "index.html",
            SdkVersion = "1.1.0",
            RequiredHostCapabilities = ["sdk-baseline-1-1.v1"],
            Views =
            [
                new SunderPackageWebViewManifest
                {
                    ViewId = "sample.web.main",
                    DisplayName = "Sample",
                    Route = "/",
                    DefaultPlacement = "middle",
                    ShowInHotbar = true,
                },
                new SunderPackageWebViewManifest
                {
                    ViewId = "sample.web.workspace",
                    DisplayName = "Workspace",
                    Route = "/workspace",
                    DefaultPlacement = "rightTop",
                    ShowInHotbar = false,
                },
            ],
        };

    private static string Header(HttpResponseMessage response, string name)
        => Assert.Single(response.Headers.GetValues(name));

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-web-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
