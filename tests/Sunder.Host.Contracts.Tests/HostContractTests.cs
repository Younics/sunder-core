using System.Text.Json;
using Sunder.Host.Contracts;
using Xunit;

namespace Sunder.Host.Contracts.Tests;

public sealed class HostContractTests
{
    [Fact]
    public void Handshake_FreezesSupportedFeatures()
    {
        var features = new List<string> { HostProtocolFeatures.RuntimeGatewayV1 };
        var handshake = new HostHandshakeResponse(
            HostProtocol.Identity,
            HostProtocol.CurrentRevision,
            HostProtocol.MinimumSupportedRevision,
            HostProtocol.MaximumSupportedRevision,
            Guid.NewGuid(),
            Guid.NewGuid(),
            features,
            new HostProductVersionDiagnostics("Sunder.Host.Supervisor", "Development", "Development"));

        features.Add(HostProtocolFeatures.RuntimeLifecycleV1);

        Assert.Equal([HostProtocolFeatures.RuntimeGatewayV1], handshake.SupportedFeatures);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)handshake.SupportedFeatures).Add(HostProtocolFeatures.RuntimeLifecycleV1));
    }

    [Fact]
    public void LifecycleContracts_UseStableWebJsonNames()
    {
        var status = new HostRuntimeStatus(
            HostRuntimeDesiredState.Running,
            HostRuntimeState.Ready,
            7,
            "1.2.3",
            "1.2.2",
            Guid.Parse("c809b7d8-9be6-4de4-9215-596336371884"),
            DateTimeOffset.Parse("2026-07-19T12:00:00Z"),
            null,
            null,
            null);

        var json = JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"desiredState\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"state\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"deploymentGeneration\":7", json, StringComparison.Ordinal);
        Assert.DoesNotContain("FailureCode", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DurableLifecycleContracts_UseStableKindsAndExpectedGeneration()
    {
        var operation = new HostOperationDescriptor(
            "11111111111111111111111111111111",
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            HostOperationKinds.RuntimeRestart,
            7,
            HostOperationState.Running,
            DateTimeOffset.Parse("2026-07-20T12:00:00Z"),
            DateTimeOffset.Parse("2026-07-20T12:00:01Z"),
            null,
            null);

        var json = JsonSerializer.Serialize(operation, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var roundTrip = JsonSerializer.Deserialize<HostOperationDescriptor>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("runtime.start", HostOperationKinds.RuntimeStart);
        Assert.Equal("runtime.stop", HostOperationKinds.RuntimeStop);
        Assert.Equal("runtime.restart", HostOperationKinds.RuntimeRestart);
        Assert.Contains("\"expectedDeploymentGeneration\":7", json, StringComparison.Ordinal);
        Assert.Equal(operation, roundTrip);
    }
}
