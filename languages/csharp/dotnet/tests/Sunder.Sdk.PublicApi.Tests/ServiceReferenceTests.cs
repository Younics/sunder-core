using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class ServiceReferenceTests
{
    [Fact]
    public async Task FullLocatorRoundtripUsesReceiverClientAndFactoryWithoutSerializingService()
    {
        var sender = new ServiceResourceTestClient();
        var receiver = new ServiceResourceTestClient();
        var senderService = new MarkerService("sender", sender);
        var reference = new ServiceProvider<MarkerService>(ServiceResourceTestClient.Provider, senderService).Reference;
        await using var senderLifetime = new SunderRpcResourceLifetime();
        await using var sending = await senderLifetime.CreateCallAsync(sender, ServiceResourceTestClient.Provider.Endpoint);
        Configure(sending);
        var wire = await sending.SerializeAsync(reference,
            static (context, value, token) => context.RegisterServiceReferenceAsync(value, token));
        Assert.Equal(new[] { "packageId", "packageVersion", "providerId", "contractId", "contractVersion",
            "descriptorSha256", "abiSha256", "activationId", "activationEpoch", "sessionGeneration", "endpoint" },
            wire.EnumerateObject().Select(property => property.Name));
        Assert.Equal(7, wire.GetProperty("sessionGeneration").GetInt64());
        Assert.Equal(ServiceResourceTestClient.Provider.Endpoint.Value, wire.GetProperty("endpoint").GetString());
        Assert.Equal(0, senderService.Accesses);

        await using var receiverLifetime = new SunderRpcResourceLifetime();
        var receiving = await receiverLifetime.CreateCallAsync(receiver, ServiceResourceTestClient.Provider.Endpoint);
        var stopped = false;
        receiving.AddServiceReference(ServiceResourceTestClient.Contract, "resource",
            (ISunderRpcClient client, SunderRpcProviderSnapshot _) => new MarkerService("receiver", client, () => stopped));
        var decoded = await receiving.DeserializeAsync<ServiceReference<MarkerService>>(wire,
            static (context, value, token) => context.PrepareServiceReferenceAsync<MarkerService>(value, token));
        await receiving.DisposeAsync();
        Assert.Equal(1, Assert.Single(receiver.Scopes).Disposals);
        Assert.NotSame(reference, decoded);
        Assert.NotSame(senderService, decoded.Service);
        Assert.Same(receiver, decoded.Service.Client);
        Assert.Equal("receiver", decoded.Service.Value);
        stopped = true;
        Assert.Throws<ObjectDisposedException>(() => decoded.Service.Value);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(reference));
    }

    [Theory]
    [InlineData("packageId")]
    [InlineData("packageVersion")]
    [InlineData("providerId")]
    [InlineData("contractId")]
    [InlineData("contractVersion")]
    [InlineData("descriptorSha256")]
    [InlineData("abiSha256")]
    [InlineData("activationId")]
    [InlineData("activationEpoch")]
    [InlineData("sessionGeneration-increased")]
    [InlineData("sessionGeneration-decreased")]
    [InlineData("endpoint")]
    [InlineData("invisible")]
    [InlineData("inactive")]
    [InlineData("faulted")]
    public async Task EveryLocatorFieldMustMatchTheVisibleActiveReceiverSnapshot(string changed)
    {
        var provider = ServiceResourceTestClient.Provider;
        var client = new ServiceResourceTestClient
        {
            Visible = changed switch
            {
                "packageId" => provider with { PackageId = "other.package" },
                "packageVersion" => provider with { PackageVersion = "1.0.1" },
                "providerId" => provider with { ProviderId = "other.provider" },
                "contractId" => provider with { ContractId = "other.contract" },
                "contractVersion" => provider with { ContractVersion = "1.0.1" },
                "descriptorSha256" => provider with { DescriptorSha256 = new string('b', 64) },
                "abiSha256" => provider with { AbiSha256 = new string('b', 64) },
                "activationId" => provider with { ActivationId = Guid.NewGuid() },
                "activationEpoch" => provider with { ActivationEpoch = provider.ActivationEpoch + 1 },
                "sessionGeneration-increased" => provider with { SessionGeneration = provider.SessionGeneration + 1 },
                "sessionGeneration-decreased" => provider with { SessionGeneration = provider.SessionGeneration - 1 },
                "endpoint" => provider with { Endpoint = new("other-endpoint") },
                "invisible" => null,
                "inactive" => provider with { State = SunderRpcProviderState.Inactive },
                "faulted" => provider with { State = SunderRpcProviderState.Faulted },
                _ => throw new InvalidOperationException(),
            },
        };
        await using var lifetime = new SunderRpcResourceLifetime();
        await using var operation = await lifetime.CreateCallAsync(client, provider.Endpoint);
        var factories = 0;
        operation.AddServiceReference(ServiceResourceTestClient.Contract, "resource",
            (ISunderRpcClient receiver, SunderRpcProviderSnapshot _) => { factories++; return new MarkerService("receiver", receiver); });
        var error = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await operation.DeserializeAsync<ServiceReference<MarkerService>>(Wire(),
                static (context, json, token) => context.PrepareServiceReferenceAsync<MarkerService>(json, token)));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, error.Error.Kind);
        Assert.Equal(0, factories);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("endpoint-only")]
    [InlineData("extra-service")]
    [InlineData("duplicate")]
    [InlineData("case-collision")]
    [InlineData("bad-id")]
    [InlineData("bad-version")]
    [InlineData("bad-hash")]
    [InlineData("bad-activation")]
    [InlineData("empty-activation")]
    [InlineData("fractional-generation")]
    [InlineData("rounded-generation")]
    [InlineData("unsafe-epoch")]
    [InlineData("negative-generation")]
    [InlineData("wrong-type")]
    [InlineData("blank-endpoint")]
    [InlineData("long-endpoint")]
    public async Task MalformedOrIncompleteLocatorsFailBeforeProviderResolution(string mutation)
    {
        var client = new ServiceResourceTestClient();
        await using var lifetime = new SunderRpcResourceLifetime();
        await using var operation = await lifetime.CreateCallAsync(client, ServiceResourceTestClient.Provider.Endpoint);
        Configure(operation);
        var value = JsonNode.Parse(Wire().GetRawText())!.AsObject();
        switch (mutation)
        {
            case "missing": value.Remove("sessionGeneration"); break;
            case "endpoint-only": value = new JsonObject { ["endpoint"] = "exact-provider" }; break;
            case "extra-service": value["service"] = new JsonObject(); break;
            case "case-collision": value["PackageId"] = "test.package"; break;
            case "bad-id": value["providerId"] = "INVALID"; break;
            case "bad-version": value["packageVersion"] = "01.0.0"; break;
            case "bad-hash": value["abiSha256"] = new string('A', 64); break;
            case "bad-activation": value["activationId"] = ServiceResourceTestClient.Provider.ActivationId.ToString("N"); break;
            case "empty-activation": value["activationId"] = Guid.Empty.ToString("D"); break;
            case "fractional-generation": value["sessionGeneration"] = 7.1; break;
            case "rounded-generation": value["sessionGeneration"] = JsonNode.Parse("7.00000000000000000000000000000000001"); break;
            case "unsafe-epoch": value["activationEpoch"] = 9_007_199_254_740_992L; break;
            case "negative-generation": value["sessionGeneration"] = -1; break;
            case "wrong-type": value["sessionGeneration"] = "7"; break;
            case "blank-endpoint": value["endpoint"] = " "; break;
            case "long-endpoint": value["endpoint"] = new string('a', 257); break;
        }
        var text = value.ToJsonString();
        if (mutation == "duplicate") text = text.Insert(1, "\"packageId\":\"test.package\",");
        using var json = JsonDocument.Parse(text);
        await Assert.ThrowsAsync<JsonException>(async () =>
            await operation.PrepareServiceReferenceAsync<MarkerService>(json.RootElement));
        Assert.Equal(0, client.Resolutions);
    }

    [Fact]
    public async Task SenderBindingIsRevalidatedAndSynchronousDecodeCannotBypassPreparation()
    {
        var client = new ServiceResourceTestClient();
        await using var lifetime = new SunderRpcResourceLifetime();
        await using var operation = await lifetime.CreateCallAsync(client, ServiceResourceTestClient.Provider.Endpoint);
        Configure(operation);
        var reference = new ServiceProvider<MarkerService>(ServiceResourceTestClient.Provider, new("sender", client)).Reference;
        await Assert.ThrowsAsync<JsonException>(async () => await operation.DeserializeAsync<ServiceReference<MarkerService>>(Wire()));
        await Assert.ThrowsAsync<JsonException>(async () => await operation.SerializeAsync(reference));
        client.Visible = client.Visible! with { SessionGeneration = 8 };
        await Assert.ThrowsAsync<SunderRpcException>(async () => await operation.SerializeAsync(reference,
            static (context, value, token) => context.RegisterServiceReferenceAsync(value, token)));
        Assert.Throws<ArgumentException>(() => operation.AddServiceReference(ServiceResourceTestClient.Contract, "missing",
            static (ISunderRpcClient receiver, SunderRpcProviderSnapshot _) => new MarkerService("receiver", receiver)));
    }

    [Fact]
    public async Task ProjectedContractIdentityIsCheckedBeforeResolutionAndCatalogRevisionIsNotLocatorIdentity()
    {
        var client = new ServiceResourceTestClient();
        await using var lifetime = new SunderRpcResourceLifetime();
        await using var operation = await lifetime.CreateCallAsync(client, ServiceResourceTestClient.Provider.Endpoint);
        Configure(operation);
        var changed = JsonNode.Parse(Wire().GetRawText())!;
        changed["contractVersion"] = "2.0.0";
        await Assert.ThrowsAsync<JsonException>(async () =>
            await operation.PrepareServiceReferenceAsync<MarkerService>(JsonSerializer.SerializeToElement(changed)));
        Assert.Equal(0, client.Resolutions);
        client.Visible = client.Visible! with { CatalogRevision = 999 };
        var result = await operation.DeserializeAsync<ServiceReference<MarkerService>>(Wire(),
            static (context, value, token) => context.PrepareServiceReferenceAsync<MarkerService>(value, token));
        Assert.Same(client, result.Service.Client);
    }

    private static void Configure(SunderRpcMarshallingContext operation)
        => operation.AddServiceReference(ServiceResourceTestClient.Contract, "resource",
            static (ISunderRpcClient receiver, SunderRpcProviderSnapshot _) => new MarkerService("receiver", receiver));

    private static JsonElement Wire()
    {
        var value = ServiceResourceTestClient.Provider;
        return JsonSerializer.SerializeToElement(new
        {
            packageId = value.PackageId, packageVersion = value.PackageVersion, providerId = value.ProviderId,
            contractId = value.ContractId, contractVersion = value.ContractVersion, descriptorSha256 = value.DescriptorSha256,
            abiSha256 = value.AbiSha256, activationId = value.ActivationId.ToString("D"), activationEpoch = value.ActivationEpoch,
            sessionGeneration = value.SessionGeneration, endpoint = value.Endpoint.Value,
        });
    }

    public sealed class MarkerService(string value, ISunderRpcClient client, Func<bool>? stopped = null)
    {
        public ISunderRpcClient Client { get; } = client;
        public int Accesses { get; private set; }
        public string Value
        {
            get { ObjectDisposedException.ThrowIf(stopped?.Invoke() == true, this); Accesses++; return value; }
        }
    }
}
