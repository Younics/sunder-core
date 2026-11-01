using System.Text.Json.Nodes;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;
using static Sunder.Runtime.Host.Tests.RuntimeRpcBrokerFixture;
using static Sunder.Runtime.Host.Tests.RuntimeRpcTestPackages;

namespace Sunder.Runtime.Host.Tests;

[Collection("Runtime RPC broker")]
public sealed class RuntimeRpcConsumerCompatibilityTests
{
    [Fact]
    public async Task ConsumerRequirements_MixedOldAndCurrentConsumersShareOneProviderWhileLegacyImportsStayExact()
    {
        var old = Contract();
        var current = EvolvedContract(old, "add-output");
        var handler = new TestHandler { Unary = (_, _) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { accepted = true, revision = "current" })) };
        var newCaller = CreatePackage("current.package", current, null, usesContract: true);
        var legacyCaller = CreatePackage("legacy.package", old, null, usesContract: true);
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(handler, callerContract: old, providerContract: current,
            transform: package => WithConsumerRequirements(package, new(old, [new("messages", "send")])), additionalPackages: [newCaller, legacyCaller]);
        foreach (var package in new[] { "caller.package", "current.package", "legacy.package" })
        {
            await fixture.GrantAsync(package, SunderRpcProtocol.DiscoverAction);
            await fixture.GrantAsync(package, SunderRpcProtocol.InvokeAction);
        }
        var oldClient = fixture.CreateClient(activation.Caller);
        var newClient = fixture.CreateClient(new(newCaller.Descriptor.PackageId, newCaller.RuntimeActivationId));
        var legacyClient = fixture.CreateClient(new(legacyCaller.Descriptor.PackageId, legacyCaller.RuntimeActivationId));
        var endpoint = Assert.Single((await oldClient.DiscoverAsync(old.ContractId)).Providers).Endpoint;
        Assert.Equal(endpoint, Assert.Single((await newClient.DiscoverAsync(old.ContractId)).Providers).Endpoint);
        Assert.Empty((await legacyClient.DiscoverAsync(old.ContractId)).Providers);
        var request = JsonSerializer.SerializeToElement(new { message = "hello" });
        Assert.False((await oldClient.InvokeAsync(endpoint, "messages", "send", request)).TryGetProperty("revision", out _));
        Assert.Equal("current", (await newClient.InvokeAsync(endpoint, "messages", "send", request)).GetProperty("revision").GetString());
        var denied = await Assert.ThrowsAsync<SunderRpcException>(() => legacyClient.InvokeAsync(endpoint, "messages", "send", request).AsTask());
        Assert.Equal(SunderRpcDispatchState.NotDispatched, denied.Error.DispatchState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsumerRequirements_DomainDetailsValidateCurrentSchemaBeforeReadingCallerShape(bool valid)
    {
        var old = Contract(declareErrorDetails: true);
        var json = JsonNode.Parse(old.GetCanonicalUtf8())!;
        json["version"] = "2.0.0";
        json["$defs"]!["ErrorDetails"]!["properties"]!["revision"] = new JsonObject { ["type"] = "string" };
        json["$defs"]!["ErrorDetails"]!["required"]!.AsArray().Add("revision");
        var current = SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(json));
        var details = valid ? JsonSerializer.SerializeToElement(new { reason = "rejected", revision = "current" })
            : JsonSerializer.SerializeToElement(new { reason = "rejected" });
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler { Unary = (_, _) => ValueTask.FromException<JsonElement>(new SunderRpcException(
            new(SunderRpcErrorKind.Domain, "messages.rejected", "rejected", details: details))) }, callerContract: old, providerContract: current,
            transform: package => WithConsumerRequirements(package, new(old, [new("messages", "send")])));
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync(old.ContractId)).Providers).Endpoint;
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(endpoint, "messages", "send", JsonSerializer.SerializeToElement(new { message = "hello" })).AsTask());
        Assert.Equal(valid ? "messages.rejected" : "rpc.provider.invalid-error-details", error.Error.Code);
        if (valid)
        {
            Assert.Equal("rejected", error.Error.Details!.Value.GetProperty("reason").GetString());
            Assert.False(error.Error.Details.Value.TryGetProperty("revision", out _));
        }
    }

    [Theory]
    [InlineData("add-output", true)]
    [InlineData("unrelated-method-removed", true)]
    [InlineData("required-method-removed", false)]
    [InlineData("request-narrowed", false)]
    [InlineData("known-output-changed", false)]
    public async Task ConsumerRequirements_AdmitOnlyTheRequiredSurfaceAndKeepOriginalPermissionIdentity(string change, bool compatible)
    {
        var source = Contract();
        var current = EvolvedContract(source, change);
        var selectors = new SunderRpcConsumerRequirements(source, [new("messages", "send")]);
        await using var fixture = new RpcFixture();
        var handler = new TestHandler { Unary = (_, _) => ValueTask.FromResult(change == "add-output"
            ? JsonSerializer.SerializeToElement(new { accepted = true, revision = "current" }) : JsonSerializer.SerializeToElement(new { accepted = true })) };
        var activation = await fixture.PublishAsync(handler, callerContract: source, providerContract: current,
            transform: package => WithConsumerRequirements(package, selectors));
        var client = fixture.CreateClient(activation.Caller);
        await Assert.ThrowsAsync<SunderRpcException>(() => client.DiscoverAsync(source.ContractId).AsTask());
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var catalog = await client.DiscoverAsync(source.ContractId);
        if (!compatible)
        {
            Assert.Empty(catalog.Providers);
            var denied = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(
                Assert.Single(fixture.Catalog.GetSnapshot().Providers).Endpoint, "messages", "send", JsonSerializer.SerializeToElement(new { message = "hello" })).AsTask());
            Assert.Equal(SunderRpcDispatchState.NotDispatched, denied.Error.DispatchState);
            return;
        }
        var provider = Assert.Single(catalog.Providers);
        Assert.Equal(current.Sha256, provider.DescriptorSha256); // Never relabel a provider as the caller's old schema.
        var response = await client.InvokeAsync(provider.Endpoint, "messages", "send", JsonSerializer.SerializeToElement(new { message = "hello" }));
        Assert.True(response.GetProperty("accepted").GetBoolean());
        Assert.False(response.TryGetProperty("revision", out _));
        await fixture.RevokeAsync(SunderRpcProtocol.InvokeAction);
        var revoked = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(provider.Endpoint, "messages", "send",
            JsonSerializer.SerializeToElement(new { message = "hello" })).AsTask());
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, revoked.Error.Kind);
    }

    [Fact]
    public async Task ConsumerRequirements_ValidateCurrentProviderOutputBeforeReadingOldResponseShape()
    {
        var source = Contract();
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler(), callerContract: source,
            providerContract: EvolvedContract(source, "add-output"),
            transform: package => WithConsumerRequirements(package, new(source, [new("messages", "send")])));
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var provider = Assert.Single((await client.DiscoverAsync(source.ContractId)).Providers);
        // The old response would pass, but omits a member required by the CURRENT provider's schema.
        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(provider.Endpoint, "messages", "send",
            JsonSerializer.SerializeToElement(new { message = "hello" })).AsTask());
        Assert.Equal("rpc.provider.invalid-output", failure.Error.Code);
        Assert.Empty(fixture.Catalog.GetSnapshot().Providers);
    }

    [Fact]
    public async Task ConsumerRequirements_StreamUsesCurrentSchemaAndCannotInvokeAnUnselectedMethod()
    {
        var source = Contract();
        var value = JsonSerializer.SerializeToElement(new { accepted = true, revision = "current" });
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler { Stream = (_, token) => Values(value, value, token) },
            callerContract: source, providerContract: EvolvedContract(source, "add-output"),
            transform: package => WithConsumerRequirements(package, new(source, [new("messages", "watch")])));
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        await fixture.GrantAsync(SunderRpcProtocol.SubscribeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync(source.ContractId)).Providers).Endpoint;
        var events = new List<JsonElement>();
        await foreach (var item in client.SubscribeAsync(endpoint, "messages", "watch", JsonSerializer.SerializeToElement(new { message = "hello" }))) events.Add(item);
        Assert.Equal(2, events.Count);
        Assert.All(events, item => { Assert.True(item.GetProperty("accepted").GetBoolean()); Assert.False(item.TryGetProperty("revision", out _)); });
        var denied = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(endpoint, "messages", "send",
            JsonSerializer.SerializeToElement(new { message = "hello" })).AsTask());
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, denied.Error.Kind);
        Assert.Equal(SunderRpcDispatchState.NotDispatched, denied.Error.DispatchState);
    }

    private static ActiveLoadedPackage WithConsumerRequirements(ActiveLoadedPackage package, SunderRpcConsumerRequirements requirements)
    {
        if (package.Descriptor.PackageId != "caller.package") return package;
        var json = JsonSerializer.SerializeToNode(package.Source.Manifest)!;
        json["usesContracts"]![0]!["consumerRequirements"] = requirements.ToJson();
        var manifest = json.Deserialize<SunderPackageManifest>()!;
        return package with { Source = package.Source with { Manifest = manifest }, RpcContractUses = manifest.UsesContracts!.Select(use => use!).ToArray() };
    }

    private static SunderRpcContractDescriptor EvolvedContract(SunderRpcContractDescriptor source, string change)
    {
        var json = JsonNode.Parse(source.GetCanonicalUtf8())!;
        json["version"] = "2.0.0";
        switch (change)
        {
            case "unrelated-method-removed": json["services"]!["messages"]!["methods"]!.AsObject().Remove("watch"); break;
            case "required-method-removed": json["services"]!["messages"]!["methods"]!.AsObject().Remove("send"); break;
            case "request-narrowed": json["$defs"]!["Request"]!["properties"]!["message"]!["maxLength"] = 16; break;
            case "known-output-changed": json["$defs"]!["Response"]!["properties"]!["accepted"]!["type"] = "string"; break;
            default:
                json["$defs"]!["Response"]!["properties"]!["revision"] = new JsonObject { ["type"] = "string" };
                json["$defs"]!["Response"]!["required"]!.AsArray().Add("revision"); break;
        }
        return SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(json));
    }
}
