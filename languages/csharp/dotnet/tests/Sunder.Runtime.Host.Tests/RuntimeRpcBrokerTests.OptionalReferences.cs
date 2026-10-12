using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RuntimeRpcBrokerTests
{
    // Exercises the SDK marshalling boundary against the actual broker/catalog and
    // invocation authority. Generated Worker framing is covered by the Build suite.
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true, "caller")]
    [InlineData(false, true, true, "source")]
    [InlineData(false, true, true, "deadline")]
    [InlineData(false, true, true, "di")]
    public async Task OptionalReference_ActualHostPreservesFallbackAndStrictLocatorCoupling(bool strict, bool midflight, bool optionalFirst, string termination = "target")
    {
        await using var fixture = new RpcFixture();
        var descriptor = OptionalHostContract();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SunderRpcContentReference? retainedContent = null;
        using var callerCancellation = new CancellationTokenSource();
        var source = new TestHandler();
        var activation = await fixture.PublishAsync(source, providerUsesContract: true,
            healthyHandler: new TestHandler(), callerContract: descriptor, providerContract: descriptor);
        foreach (var action in new[] { SunderRpcProtocol.DiscoverAction, SunderRpcProtocol.InvokeAction })
        {
            await fixture.GrantAsync(action);
            await fixture.GrantAsync("provider.package", action);
        }
        var client = fixture.CreateClient(activation.Caller);
        var receiverClient = fixture.CreateClient(activation.Provider);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await using var receiverBindings = new SunderRpcServiceBindingLifetime(receiverClient);
        var providers = await bindings.DiscoverAsync(descriptor.ContractId);
        var target = providers.Providers.Single(provider => provider.PackageId == "healthy.package");
        var callee = providers.Providers.Single(provider => provider.PackageId == "provider.package");
        var entry = bindings.CreateProvider(target, static (_, _, token) => new OptionalHostTarget(token));
        var first = entry.Reference;
        var second = entry.Reference; // Distinct wrappers, identical canonical locator.
        var calleeReference = bindings.CreateProvider(callee, static (_, _, token) => new OptionalHostTarget(token)).Reference;
        await using var resources = new SunderRpcResourceLifetime(bindings);
        await using var sending = await resources.CreateCallAsync(client, callee.Endpoint,
            new(DateTimeOffset.UtcNow.AddSeconds(termination == "deadline" ? 2 : 30)), callerCancellation.Token,
            providerRetirementToken: calleeReference.RetirementToken);
        ConfigureOptionalHost(sending, bindings, descriptor);
        source.UnaryWithContext = async (invocation, wire, token) =>
        {
            await using var receiving = SunderRpcMarshallingContext.ForInvocation(invocation, receiverClient, receiverBindings);
            ConfigureOptionalHost(receiving, receiverBindings, descriptor);
            var value = await receiving.DeserializeAsync<OptionalHostPayload>(wire, async (context, json, cancellation) =>
            {
                if (optionalFirst) await context.PrepareOptionalServiceReferenceAsync<OptionalHostTarget>(json.GetProperty("optional"), cancellation);
                await context.PrepareServiceReferenceAsync<OptionalHostTarget>(json.GetProperty("required"), cancellation);
                if (!optionalFirst) await context.PrepareOptionalServiceReferenceAsync<OptionalHostTarget>(json.GetProperty("optional"), cancellation);
            });
            await using var content = new MemoryStream(new byte[] { 1, 2, 3 });
            retainedContent = await invocation.RegisterContentAsync(content,
                new SunderRpcContentRegistrationOptions("application/octet-stream", "retained.bin", 3,
                    Repeatability: SunderRpcContentRepeatability.Repeatable, MaximumUses: 2), token);
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (value.Optional is not null)
            {
                await ObserveOptionalHostCancellation(value.Optional.RetirementToken);
                Assert.Throws<SunderRpcException>(() => value.Optional.Service);
            }
            if (!strict && termination == "target")
            {
                Assert.False(token.IsCancellationRequested);
                Assert.False(receiving.CancellationToken.IsCancellationRequested);
            }
            return await receiving.SerializeAsync(new { accepted = true });
        };
        void RetireTarget() => Assert.True(fixture.Catalog.DeactivatePackage(target.PackageId, target.ActivationId, faulted: false));
        if (!midflight)
        {
            RetireTarget();
            await ObserveOptionalHostCancellation(first.RetirementToken);
        }
        async Task<JsonElement> Run()
        {
            var wire = await sending.SerializeAsync(new OptionalHostPayload(first, strict ? second : null), async (context, value, token) =>
            {
                if (optionalFirst) await context.RegisterOptionalServiceReferenceAsync(value.Optional, token);
                await context.RegisterServiceReferenceAsync(value.Required, token);
                if (!optionalFirst) await context.RegisterOptionalServiceReferenceAsync(value.Optional, token);
            });
            if (!midflight) Assert.Equal(JsonValueKind.Null, wire.GetProperty("optional").ValueKind);
            await SunderRpcServiceCall.EnsureCurrentAsync((ISunderRpcCallScope)sending.Client!, callee, sending.CancellationToken);
            return await sending.Client!.InvokeAsync(callee.Endpoint, "messages", "send", wire, cancellationToken: sending.CancellationToken);
        }
        var call = Run();
        if (midflight)
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            RetireTarget();
            await ObserveOptionalHostCancellation(first.RetirementToken);
        }
        if (termination == "caller") await callerCancellation.CancelAsync();
        if (termination == "source") Assert.True(fixture.Catalog.DeactivatePackage(callee.PackageId, callee.ActivationId, faulted: false));
        if (termination == "di") await resources.DisposeAsync();
        if (termination == "deadline") await Task.Delay(TimeSpan.FromSeconds(2.1));
        release.TrySetResult();
        if (strict || termination != "target")
        {
            var error = await Record.ExceptionAsync(async () => await call.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(error is SunderRpcException or OperationCanceledException, error?.ToString() ?? "Strict reference unexpectedly returned fallback.");
            if (!midflight) Assert.False(entered.Task.IsCompleted);
        }
        else
        {
            Assert.True((await call.WaitAsync(TimeSpan.FromSeconds(10))).GetProperty("accepted").GetBoolean());
            Assert.False(sending.CancellationToken.IsCancellationRequested);
            Assert.False(calleeReference.RetirementToken.IsCancellationRequested);
            var scope = (ISunderRpcCallScope)sending.Client!;
            await using var retained = await scope.OpenContentAsync(retainedContent!);
            Assert.Equal(1, retained.ReadByte());
            Assert.True(fixture.Catalog.DeactivatePackage(callee.PackageId, callee.ActivationId, faulted: false));
            await ObserveOptionalHostCancellation(calleeReference.RetirementToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await retained.ReadAsync(new byte[1]));
        }
    }

    [Fact]
    public async Task OptionalReference_ActualHostFreshPermissionDenialIsNotAbsenceAfterRetirement()
    {
        await using var fixture = new RpcFixture();
        var descriptor = OptionalHostContract();
        var activation = await fixture.PublishAsync(new TestHandler(), healthyHandler: new TestHandler(),
            callerContract: descriptor, providerContract: descriptor);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        var providers = await bindings.DiscoverAsync(descriptor.ContractId);
        var target = providers.Providers.Single(provider => provider.PackageId == "healthy.package");
        var source = providers.Providers.Single(provider => provider.PackageId == "provider.package");
        var reference = bindings.CreateProvider(target, static (_, _, token) => new OptionalHostTarget(token)).Reference;
        Assert.True(fixture.Catalog.DeactivatePackage(target.PackageId, target.ActivationId, faulted: false));
        await ObserveOptionalHostCancellation(reference.RetirementToken);
        await using var resources = new SunderRpcResourceLifetime(bindings);
        await using var sending = await resources.CreateCallAsync(client, source.Endpoint);
        ConfigureOptionalHost(sending, bindings, descriptor);
        await fixture.RevokeAsync(SunderRpcProtocol.DiscoverAction);
        var error = await Assert.ThrowsAsync<SunderRpcException>(async () => await sending.SerializeAsync(reference,
            static (context, value, token) => context.RegisterOptionalServiceReferenceAsync(value, token)));
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, error.Error.Kind);
        Assert.Equal(SunderRpcDispatchState.NotDispatched, error.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Safe, error.Error.RetrySafety);
    }

    private sealed record OptionalHostPayload(ServiceReference<OptionalHostTarget>? Optional, ServiceReference<OptionalHostTarget>? Required);
    private sealed record OptionalHostTarget(CancellationToken RetirementToken);

    private static void ConfigureOptionalHost(SunderRpcMarshallingContext context, SunderRpcServiceBindingLifetime bindings, SunderRpcContractDescriptor descriptor)
        => context.AddServiceReference(descriptor, "messages", bindings, static (_, _, token) => new OptionalHostTarget(token));

    private static async Task ObserveOptionalHostCancellation(CancellationToken token)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => cancelled.TrySetResult());
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static SunderRpcContractDescriptor OptionalHostContract()
    {
        var document = JsonNode.Parse(Contract().GetCanonicalUtf8())!;
        var properties = new JsonObject();
        foreach (var name in new[] { "packageId", "packageVersion", "providerId", "contractId", "contractVersion", "descriptorSha256", "abiSha256", "activationId", "endpoint" })
            properties[name] = new JsonObject { ["type"] = "string" };
        foreach (var name in new[] { "activationEpoch", "sessionGeneration" })
            properties[name] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 9007199254740991L };
        document["$defs"]!["Reference"] = new JsonObject { ["type"] = "object", ["properties"] = properties,
            ["required"] = new JsonArray(properties.Select(pair => (JsonNode?)JsonValue.Create(pair.Key)).ToArray()), ["additionalProperties"] = false };
        JsonObject NullableReference() => new() { ["oneOf"] = new JsonArray(new JsonObject { ["$ref"] = "#/$defs/Reference" }, new JsonObject { ["type"] = "null" }) };
        document["$defs"]!["Request"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["optional"] = NullableReference(), ["required"] = NullableReference() },
            ["required"] = new JsonArray("optional", "required"), ["additionalProperties"] = false };
        return SunderRpcContractDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()));
    }
}
