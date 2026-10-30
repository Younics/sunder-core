using System.Text.Json;
using Sunder.Host.Client;
using Sunder.Host.Contracts;
using Xunit;

namespace Sunder.Host.Client.Tests;

public sealed class HostContractTests
{
    [Theory]
    [InlineData(1, 1, 1, true)]
    [InlineData(2, 1, 2, true)]
    [InlineData(1, 1, 2, true)]
    [InlineData(2, 2, 2, false)]
    [InlineData(0, 0, 1, false)]
    [InlineData(1, 2, 1, false)]
    [InlineData(3, 1, 2, false)]
    [InlineData(0, 1, 2, false)]
    public void ProtocolRevision1_AcceptsValidServerRangesContainingV1(int revision, int minimum, int maximum, bool compatible)
    {
        var handshake = new HostHandshakeResponse(
            HostProtocol.Identity,
            revision,
            minimum,
            maximum,
            Guid.NewGuid(),
            Guid.NewGuid(),
            [HostProtocolFeatures.RuntimeGatewayV1],
            new HostProductVersionDiagnostics("Sunder.Host.Supervisor", "Development", "Development"));

        Assert.Equal(1, HostProtocol.CurrentRevision);
        Assert.Equal(compatible, HostProtocolCompatibility.GetIncompatibility(handshake) is null);
        Assert.NotNull(HostProtocolCompatibility.GetIncompatibility(handshake, HostProtocolFeatures.RuntimeLifecycleV1));
        Assert.NotNull(HostProtocolCompatibility.GetIncompatibility(handshake with { ProtocolIdentity = "other" }));
        Assert.NotNull(HostProtocolCompatibility.GetIncompatibility(handshake with { SupervisorInstanceId = Guid.Empty }));
    }

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
    public void Handshake_DeploymentIdentityIsAdditiveAndUsesStableJsonName()
    {
        var identity = HostDeploymentIdentity.FromSha256(new string('a', 64));
        var handshake = new HostHandshakeResponse(
            HostProtocol.Identity,
            HostProtocol.CurrentRevision,
            HostProtocol.MinimumSupportedRevision,
            HostProtocol.MaximumSupportedRevision,
            Guid.NewGuid(),
            Guid.NewGuid(),
            [HostProtocolFeatures.RuntimeGatewayV1],
            new HostProductVersionDiagnostics("Sunder.Host.Supervisor", "1.0.0", "1.0.0"))
        {
            DeploymentIdentity = identity,
        };

        var json = JsonSerializer.Serialize(handshake, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var legacy = JsonSerializer.Deserialize<HostHandshakeResponse>(
            json.Replace($",\"deploymentIdentity\":\"{identity}\"", string.Empty, StringComparison.Ordinal),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains($"\"deploymentIdentity\":\"{identity}\"", json, StringComparison.Ordinal);
        Assert.Null(legacy?.DeploymentIdentity);
        Assert.True(HostDeploymentIdentity.IsValid(identity));
    }

    [Fact]
    public void LifecycleContracts_UseStableWebJsonNames()
    {
        var status = new HostRuntimeStatus(
            HostRuntimeDesiredState.Running,
            HostRuntimeState.Ready,
            7,
            Guid.Parse("c809b7d8-9be6-4de4-9215-596336371884"),
            null,
            null,
            null);

        var json = JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"desiredState\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"state\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"deploymentGeneration\":7", json, StringComparison.Ordinal);
        Assert.DoesNotContain("activeVersion", json, StringComparison.Ordinal);
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
