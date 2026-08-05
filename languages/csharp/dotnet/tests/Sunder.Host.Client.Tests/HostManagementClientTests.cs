using System.Net;
using System.Net.Http.Json;
using Sunder.Host.Client;
using Sunder.Host.Contracts;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Host.Client.Tests;

public sealed class HostManagementClientTests
{
    [Fact]
    public async Task Client_SubmitsAndPollsDurableLifecycleOperation()
    {
        var operationId = "11111111111111111111111111111111";
        var mutation = new HostLifecycleRequest(Guid.NewGuid(), 4);
        var accepted = CreateOperation(operationId, mutation, HostOperationState.Accepted);
        var succeeded = accepted with
        {
            State = HostOperationState.Succeeded,
            UpdatedAtUtc = accepted.UpdatedAtUtc.AddSeconds(1),
            Message = "Runtime worker is ready.",
        };
        var submission = new HostLifecycleSubmission(accepted);
        var requests = new List<(HttpMethod Method, string Uri, string? Authorization)>();
        using var client = new HostManagementClient(
            () => new RuntimeConnectionInfo(new Uri("https://host.example:5275/"), "client-token"),
            new RecordingHandler(request =>
            {
                requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString()));
                if (request.Method == HttpMethod.Post)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Accepted)
                    {
                        Content = JsonContent.Create(submission),
                    };
                    response.Headers.Location = new Uri($"/api/host/v1/operations/{operationId}", UriKind.Relative);
                    return Task.FromResult(response);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(succeeded),
                });
            }));

        var response = await client.SubmitRestartRuntimeAsync(mutation);
        var completed = await client.WaitForOperationAsync(response.Operation);

        Assert.Equal(succeeded, completed);
        Assert.Equal(
            [
                (HttpMethod.Post, "https://host.example:5275/api/host/v1/runtime/restart", "Bearer client-token"),
                (HttpMethod.Get, $"https://host.example:5275/api/host/v1/operations/{operationId}", "Bearer client-token"),
            ],
            requests);
    }

    [Fact]
    public async Task Client_RejectsLifecycleSubmissionWithoutMatchingLocation()
    {
        var request = new HostLifecycleRequest(Guid.NewGuid(), 4);
        var operation = CreateOperation(
            "11111111111111111111111111111111",
            request,
            HostOperationState.Accepted) with
        { Kind = HostOperationKinds.RuntimeStart };
        using var client = new HostManagementClient(
            () => new RuntimeConnectionInfo(new Uri("https://host.example:5275/"), "client-token"),
            new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(new HostLifecycleSubmission(operation)),
            })));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.SubmitStartRuntimeAsync(request));
    }

    [Fact]
    public async Task Client_ShutsDownHostWithBearerAuthentication()
    {
        HttpRequestMessage? recorded = null;
        using var client = new HostManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), "client-token"),
            new RecordingHandler(request =>
            {
                recorded = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }));

        await client.ShutdownHostAsync();

        Assert.Equal(HttpMethod.Post, recorded!.Method);
        Assert.Equal("http://127.0.0.1:5275/api/host/v1/shutdown", recorded.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer client-token", recorded.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Client_RequestsBoundedShutdownForUninstall()
    {
        HttpRequestMessage? recorded = null;
        using var client = new HostManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), "client-token"),
            new RecordingHandler(request =>
            {
                recorded = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }));

        await client.ShutdownHostForUninstallAsync();

        Assert.Equal(HttpMethod.Post, recorded!.Method);
        Assert.Equal(
            "http://127.0.0.1:5275/api/host/v1/shutdown?mode=uninstall",
            recorded.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer client-token", recorded.Headers.Authorization!.ToString());
    }

    [Fact]
    public void Compatibility_RequiresHostIdentityRangeAndRequestedFeatures()
    {
        var compatible = new HostHandshakeResponse(
            HostProtocol.Identity,
            HostProtocol.CurrentRevision,
            HostProtocol.MinimumSupportedRevision,
            HostProtocol.MaximumSupportedRevision,
            Guid.NewGuid(),
            Guid.NewGuid(),
            [HostProtocolFeatures.RuntimeGatewayV1],
            new HostProductVersionDiagnostics("Sunder.Host.Supervisor", "1.0.0", "1.0.0"));

        Assert.Null(HostProtocolCompatibility.GetIncompatibility(compatible));
        Assert.Null(HostProtocolCompatibility.GetIncompatibility(
            compatible,
            HostProtocolFeatures.RuntimeGatewayV1));
        Assert.NotNull(HostProtocolCompatibility.GetIncompatibility(
            compatible,
            HostProtocolFeatures.RuntimeLifecycleV1));
        Assert.NotNull(HostProtocolCompatibility.GetIncompatibility(compatible with { HostId = Guid.Empty }));
    }

    private static HostRuntimeStatus CreateStatus()
        => new(
            HostRuntimeDesiredState.Running,
            HostRuntimeState.Ready,
            4,
            Guid.NewGuid(),
            null,
            null,
            null);

    private static HostOperationDescriptor CreateOperation(
        string operationId,
        HostLifecycleRequest request,
        HostOperationState state)
        => new(
            operationId,
            request.MutationId,
            HostOperationKinds.RuntimeRestart,
            request.ExpectedDeploymentGeneration,
            state,
            DateTimeOffset.Parse("2026-07-20T12:00:00Z"),
            DateTimeOffset.Parse("2026-07-20T12:00:00Z"),
            null,
            null);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => sendAsync(request);
    }
}
