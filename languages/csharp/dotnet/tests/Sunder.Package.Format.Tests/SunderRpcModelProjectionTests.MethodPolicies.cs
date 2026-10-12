using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed partial class SunderRpcModelProjectionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2100)]
    public void MethodPolicies_RoundTripPinsHintsWithoutChangingDescriptorOrAbi(int timeoutSeconds)
    {
        var descriptor = Descriptor("example.first");
        var descriptorBytes = descriptor.GetCanonicalUtf8();
        var abiBytes = descriptor.GetCanonicalAbiProjectionUtf8();
        var projection = Projection("Value", "SharedValue");
        var original = Entry(descriptor, projection);
        var hinted = Entry(descriptor, projection, MethodPolicies(timeoutSeconds));
        var document = new SunderRpcModelProjectionDocument(1, "example.owner", "1.0.0", [hinted]);
        var parsed = SunderRpcModelProjectionDocument.Parse(document.ToCanonicalUtf8());
        parsed.Validate(Manifest(descriptor), new Dictionary<string, SunderRpcContractDescriptor> { [descriptor.ContractId] = descriptor });
        Assert.Equal(timeoutSeconds, Assert.Single(parsed.Contracts).MethodPolicies.Services["example"]["invoke"].TimeoutSeconds);
        Assert.Equal(new[] { "contractId", "contractVersion", "descriptorSha256", "abiSha256", "modelProjection", "methodPolicies" },
            hinted.ToJson().Select(property => property.Key));
        Assert.Equal(descriptorBytes, descriptor.GetCanonicalUtf8());
        Assert.Equal(abiBytes, descriptor.GetCanonicalAbiProjectionUtf8());
        Assert.Equal(original.DescriptorSha256, hinted.DescriptorSha256);
        Assert.Equal(original.AbiSha256, hinted.AbiSha256);
        Assert.False(original.ToCanonicalUtf8().AsSpan().SequenceEqual(hinted.ToCanonicalUtf8()));
        Assert.NotEqual(new SunderRpcModelProjectionDocument(1, "example.owner", "1.0.0", [original]).ModelProjectionSha256,
            document.ModelProjectionSha256);
        var reordered = document.ToJson();
        var contractJson = reordered["contracts"]![0]!.AsObject();
        var models = contractJson["modelProjection"]!.DeepClone();
        contractJson.Remove("modelProjection"); contractJson.Add("modelProjection", models);
        Assert.Throws<InvalidDataException>(() => SunderRpcModelProjectionDocument.Parse(SunderRpcModelProjectionJson.Write(reordered)));

        // Policy differences do not become model-shape differences across contracts.
        var other = Descriptor("example.second");
        SunderRpcModelProjectionGraph.Validate([hinted, Entry(other, projection)],
            new[] { descriptor, other }.ToDictionary(d => d.ContractId),
            new Dictionary<string, string> { [descriptor.ContractId] = "example.owner", [other.ContractId] = "example.owner" });
    }

    [Fact]
    public void MethodPolicies_EmptyIsOmittedAndNestedSnapshotsCannotChangeRoleEvidence()
    {
        var descriptor = Descriptor("example.first");
        var original = Entry(descriptor, SunderRpcModelProjection.Empty);
        var explicitEmpty = Entry(descriptor, SunderRpcModelProjection.Empty, SunderRpcMethodPolicies.Empty);
        Assert.True(original.MethodPolicies.IsEmpty);
        Assert.False(original.ToJson().ContainsKey("methodPolicies"));
        Assert.Equal(original.ToCanonicalUtf8(), explicitEmpty.ToCanonicalUtf8());
        var explicitEmptyJson = new SunderRpcModelProjectionDocument(1, "example.owner", "1.0.0", [original]).ToJson();
        explicitEmptyJson["contracts"]![0]!["methodPolicies"] = new JsonObject();
        Assert.Throws<InvalidDataException>(() => SunderRpcModelProjectionDocument.Parse(SunderRpcModelProjectionJson.Write(explicitEmptyJson)));
        var methods = new Dictionary<string, SunderRpcMethodPolicy> { ["invoke"] = new(2100) };
        var services = new Dictionary<string, IReadOnlyDictionary<string, SunderRpcMethodPolicy>> { ["example"] = methods };
        var policies = new SunderRpcMethodPolicies(services);
        var firstRole = Entry(descriptor, SunderRpcModelProjection.Empty, policies);
        var bytes = firstRole.ToCanonicalUtf8();
        methods["invoke"] = new(1); services.Clear();
        policies.ToJson()["example"]!["invoke"]!["timeoutSeconds"] = 1;
        Assert.Equal(bytes, firstRole.ToCanonicalUtf8());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, SunderRpcMethodPolicy>)policies.Services["example"])["invoke"] = new(1));
        var differentRole = Entry(descriptor, SunderRpcModelProjection.Empty, MethodPolicies(2099));
        Assert.False(bytes.AsSpan().SequenceEqual(differentRole.ToCanonicalUtf8()));
        Assert.Equal(firstRole.ModelProjection.ToCanonicalUtf8(), differentRole.ModelProjection.ToCanonicalUtf8());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"example\":{}}")]
    [InlineData("{\"example\":{\"invoke\":{}}}")]
    [InlineData("{\"example\":{\"invoke\":{\"timeoutSeconds\":0}}}")]
    [InlineData("{\"example\":{\"invoke\":{\"timeoutSeconds\":-1}}}")]
    [InlineData("{\"example\":{\"invoke\":{\"timeoutSeconds\":2101}}}")]
    [InlineData("{\"example\":{\"invoke\":{\"timeoutSeconds\":1.5}}}")]
    [InlineData("{\"example\":{\"invoke\":{\"timeoutSeconds\":1e0}}}")]
    [InlineData("{\"example\":{\"invoke\":{\"timeoutSeconds\":null}}}")]
    [InlineData("{\"example\":{\"invoke\":{\"timeoutSeconds\":\"1\"}}}")]
    [InlineData("{\"example\":{\"invoke\":{\"timeoutSeconds\":1,\"timeoutSeconds\":2}}}")]
    [InlineData("{\"example\":{\"invoke\":{\"timeoutSeconds\":1,\"required\":true}}}")]
    [InlineData("{\"Example\":{\"invoke\":{\"timeoutSeconds\":1}}}")]
    [InlineData("{\"example\":{\"_invoke\":{\"timeoutSeconds\":1}}}")]
    [InlineData("{\"z\":{\"invoke\":{\"timeoutSeconds\":1}},\"a\":{\"invoke\":{\"timeoutSeconds\":1}}}")]
    [InlineData("{\"example\":{\"z\":{\"timeoutSeconds\":1},\"a\":{\"timeoutSeconds\":1}}}")]
    [InlineData("{\"example\":{\"invoke\":{\"timeoutSeconds\":1},\"invoke\":{\"timeoutSeconds\":2}}}")]
    public void MethodPolicies_RejectNoncanonicalEmptyOrInvalidHints(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => SunderRpcMethodPolicies.Parse(document.RootElement));
    }

    [Theory]
    [InlineData("example", "missing")]
    [InlineData("missing", "invoke")]
    public void MethodPolicies_ResolveEveryTargetInTheExactDescriptor(string service, string method)
    {
        var descriptor = Descriptor("example.first");
        var policies = MethodPolicies(2100, service, method);
        var document = new SunderRpcModelProjectionDocument(1, "example.owner", "1.0.0", [Entry(descriptor, SunderRpcModelProjection.Empty, policies)]);
        var parsed = SunderRpcModelProjectionDocument.Parse(document.ToCanonicalUtf8());
        Assert.Throws<InvalidDataException>(() => parsed.Validate(Manifest(descriptor),
            new Dictionary<string, SunderRpcContractDescriptor> { [descriptor.ContractId] = descriptor }));
    }

    [Theory]
    [InlineData(1, 512)]
    [InlineData(64, 8)]
    public void MethodPolicies_UseActualDescriptorServiceAndTotalMethodLimits(int serviceCount, int methodCount)
    {
        var json = JsonNode.Parse(Descriptor("example.first").GetCanonicalUtf8())!;
        var methodTemplate = json["services"]!["example"]!["methods"]!["invoke"]!;
        var services = new JsonObject();
        var policies = new JsonObject();
        for (var service = 0; service < serviceCount; service++)
        {
            var methods = new JsonObject(); var hints = new JsonObject();
            for (var method = 0; method < methodCount; method++)
            {
                methods.Add($"m{method:000}", methodTemplate.DeepClone());
                hints.Add($"m{method:000}", new SunderRpcMethodPolicy(2100).ToJson());
            }
            services.Add($"s{service:00}", new JsonObject { ["methods"] = methods });
            policies.Add($"s{service:00}", hints);
        }
        json["services"] = services;
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
        using (var parsed = JsonDocument.Parse(policies.ToJsonString())) SunderRpcMethodPolicies.Parse(parsed.RootElement).Validate(descriptor);
        // Each map is bounded and the combined method count is bounded independently.
        policies[$"s{serviceCount - 1:00}"]![$"m{methodCount:000}"] = new SunderRpcMethodPolicy(2100).ToJson();
        using var overflow = JsonDocument.Parse(policies.ToJsonString());
        Assert.Throws<InvalidDataException>(() => SunderRpcMethodPolicies.Parse(overflow.RootElement));
        var tooManyServices = new JsonObject();
        for (var index = 0; index < 65; index++) tooManyServices[$"s{index:00}"] = MethodPolicies(1).ToJson()["example"]!.DeepClone();
        using var serviceOverflow = JsonDocument.Parse(tooManyServices.ToJsonString());
        Assert.Throws<InvalidDataException>(() => SunderRpcMethodPolicies.Parse(serviceOverflow.RootElement));
    }

    private static SunderRpcMethodPolicies MethodPolicies(int seconds, string service = "example", string method = "invoke")
        => new(new Dictionary<string, IReadOnlyDictionary<string, SunderRpcMethodPolicy>>
        { [service] = new Dictionary<string, SunderRpcMethodPolicy> { [method] = new(seconds) } });
}
