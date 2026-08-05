using System.Net;
using System.Net.Http.Json;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimePackageSettingsClientTests
{
    [Fact]
    public async Task SettingsOperations_UseDedicatedAuthenticatedRoutes()
    {
        var requests = new List<(HttpMethod Method, string Path, string? Token)>();
        using var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/handshake")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new RuntimeHandshakeResponse(
                        RuntimeProtocol.Identity,
                        RuntimeProtocol.CurrentRevision,
                        RuntimeProtocol.MinimumSupportedRevision,
                        RuntimeProtocol.MaximumSupportedRevision,
                        Guid.NewGuid(),
                        [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.AtomicPackageSnapshotV1],
                        new RuntimeProductVersionDiagnostics("Sunder.Runtime.Host", "Development", "Development"))),
                };
            }
            requests.Add((
                request.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.Parameter));
            return request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new PackageSettingValueResponse(
                        IsStored: false,
                        StoredValue: null,
                        EffectiveValue: "schema-default")),
                }
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var client = new RuntimePackageDataClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5123/"), "test-token"),
            handler);

        var value = await client.GetSettingAsync("test.package", "feature.enabled");
        await client.SetSettingAsync("test.package", "feature.enabled", "false");
        await client.DeleteSettingAsync("test.package", "feature.enabled");

        Assert.False(value!.IsStored);
        Assert.Equal("schema-default", value.EffectiveValue);
        Assert.Collection(
            requests,
            request => AssertRequest(request, HttpMethod.Get),
            request => AssertRequest(request, HttpMethod.Put),
            request => AssertRequest(request, HttpMethod.Delete));
    }

    private static void AssertRequest(
        (HttpMethod Method, string Path, string? Token) request,
        HttpMethod expectedMethod)
    {
        Assert.Equal(expectedMethod, request.Method);
        Assert.Equal("/api/v1/packages/test.package/settings/feature.enabled", request.Path);
        Assert.Equal("test-token", request.Token);
        Assert.DoesNotContain("/data/", request.Path, StringComparison.Ordinal);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(send(request));
        }
    }
}
