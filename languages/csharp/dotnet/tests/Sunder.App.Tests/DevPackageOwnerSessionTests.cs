using System.Net;
using System.Net.Http.Json;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Xunit;
using AppDevPackageOwnerSession = Sunder.App.Services.DevPackageOwnerSession;

namespace Sunder.App.Tests;

public sealed class DevPackageOwnerSessionTests
{
    [Fact]
    public async Task AppSession_DelegatesOwnershipToSharedRuntimeClientSession()
    {
        var runtimeId = Guid.NewGuid();
        DevPackageOwnerMutationRequest? mutation = null;
        var handler = new DelegateHandler(async request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/handshake":
                    return Json(new RuntimeHandshakeResponse(
                        RuntimeProtocol.Identity,
                        RuntimeProtocol.CurrentRevision,
                        RuntimeProtocol.MinimumSupportedRevision,
                        RuntimeProtocol.MaximumSupportedRevision,
                        runtimeId,
                        [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.OfficialRuntimeDownloadsV1, RuntimeProtocolFeatures.DevPackageOwnerLeasesV1],
                        new RuntimeProductVersionDiagnostics("Sunder.Runtime.Host", "1.0.0", "test")));
                case var path when path.StartsWith("/api/v1/dev-package-owners/", StringComparison.Ordinal)
                                   && !path.EndsWith("/release", StringComparison.Ordinal):
                    mutation = await request.Content!.ReadFromJsonAsync<DevPackageOwnerMutationRequest>();
                    var ownerId = path.Split('/')[^1];
                    return Json(new DevPackageOwnerLeaseResponse(
                        runtimeId,
                        ownerId,
                        mutation!.MutationId,
                        mutation.Revision,
                        DateTimeOffset.UtcNow.AddMinutes(1),
                        1,
                        [new DevPackageOwnerPackage("demo", false)]));
                case var path when path.EndsWith("/release", StringComparison.Ordinal):
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        });
        var connection = new RuntimeConnectionInfo(
            new Uri("http://127.0.0.1:5275/"),
            "runtime-bearer-token");
        using var transport = new RuntimeClientTransport(() => connection, handler);
        await using var session = new AppDevPackageOwnerSession(transport);

        var lease = await session.AcquireAsync([Path.Combine(".", "dev-a")], watch: false);
        await session.ReleaseAsync();

        Assert.Equal("demo", Assert.Single(lease.Packages).PackageId);
        Assert.NotNull(mutation);
        Assert.NotEqual(connection.BearerToken, mutation.OwnerToken);
        Assert.Equal(Environment.ProcessId, mutation.ProcessId);
        Assert.NotNull(mutation.ProcessStartedAtUtc);
    }

    [Fact]
    public void AppSession_HoldsOnlyTheSharedOwnerSessionImplementation()
    {
        var field = Assert.Single(typeof(AppDevPackageOwnerSession)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));

        Assert.Equal(typeof(Sunder.Runtime.Client.DevPackageOwnerSession), field.FieldType);
    }

    private static HttpResponseMessage Json<T>(T value)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => send(request);
    }
}
