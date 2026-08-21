using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeRpcValueMeasurementTests
{
    [Fact]
    public void Utf8Bytes_AcceptsExactLimitAndRejectsOneByteOver()
    {
        var limits = Limits(bytes: 3);

        var exact = RuntimeRpcValueMeasurer.Measure(
            JsonSerializer.SerializeToElement("a"),
            limits);
        var exception = Assert.Throws<RuntimeRpcValueLimitException>(() =>
            RuntimeRpcValueMeasurer.Measure(
                JsonSerializer.SerializeToElement("aa"),
                limits));

        Assert.Equal(3, exact.Utf8Bytes);
        Assert.Equal(RuntimeRpcValueLimitKind.Bytes, exception.LimitKind);
    }

    [Fact]
    public void Utf8Bytes_UsesCompactWorkerEncodingAndPreservesPortableJsonNumbers()
    {
        using var document = JsonDocument.Parse(
            "{\"text\":\"é<&\\n\",\"numbers\":[1E+30,-0,1.2300]}");
        var expected = JsonSerializer.SerializeToUtf8Bytes(
            document.RootElement,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

        var measurement = RuntimeRpcValueMeasurer.Measure(
            document.RootElement,
            Limits(bytes: expected.Length));

        Assert.Equal(expected.Length, measurement.Utf8Bytes);
        Assert.True(measurement.Utf8Bytes > document.RootElement.GetRawText().Length);
    }

    [Fact]
    public void Utf8Bytes_LargeTokenAcceptsExactLimitAndRejectsOneByteOver()
    {
        var exactValue = JsonSerializer.SerializeToElement(new string('a', 64 * 1024));
        var overValue = JsonSerializer.SerializeToElement(new string('a', 64 * 1024 + 1));
        var exactBytes = JsonSerializer.SerializeToUtf8Bytes(
            exactValue,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }).Length;
        var limits = Limits(bytes: exactBytes);

        Assert.Equal(
            exactBytes,
            RuntimeRpcValueMeasurer.Measure(exactValue, limits).Utf8Bytes);
        var exception = Assert.Throws<RuntimeRpcValueLimitException>(() =>
            RuntimeRpcValueMeasurer.Measure(overValue, limits));
        Assert.Equal(RuntimeRpcValueLimitKind.Bytes, exception.LimitKind);
    }

    [Fact]
    public void Depth_AcceptsExactLimitAndRejectsNextLevel()
    {
        using var exact = JsonDocument.Parse("[[]]");
        using var over = JsonDocument.Parse("[[[]]]");
        var limits = Limits(depth: 2);

        Assert.Equal(2, RuntimeRpcValueMeasurer.Measure(exact.RootElement, limits).MaximumDepth);
        var exception = Assert.Throws<RuntimeRpcValueLimitException>(() =>
            RuntimeRpcValueMeasurer.Measure(over.RootElement, limits));
        Assert.Equal(RuntimeRpcValueLimitKind.Depth, exception.LimitKind);
    }

    [Fact]
    public void Nodes_AcceptsExactLimitAndRejectsOneNodeOver()
    {
        using var exact = JsonDocument.Parse("[0,0]");
        using var over = JsonDocument.Parse("[0,0,0]");
        var limits = Limits(nodes: 3);

        Assert.Equal(3, RuntimeRpcValueMeasurer.Measure(exact.RootElement, limits).TotalNodes);
        var exception = Assert.Throws<RuntimeRpcValueLimitException>(() =>
            RuntimeRpcValueMeasurer.Measure(over.RootElement, limits));
        Assert.Equal(RuntimeRpcValueLimitKind.Nodes, exception.LimitKind);
    }

    [Fact]
    public void ObjectMembers_AcceptsExactLimitAndRejectsOneMemberOver()
    {
        using var exact = JsonDocument.Parse("{\"a\":0,\"b\":0}");
        using var over = JsonDocument.Parse("{\"a\":0,\"b\":0,\"c\":0}");
        var limits = Limits(members: 2);

        Assert.Equal(
            2,
            RuntimeRpcValueMeasurer.Measure(exact.RootElement, limits).MaximumObjectMembers);
        var exception = Assert.Throws<RuntimeRpcValueLimitException>(() =>
            RuntimeRpcValueMeasurer.Measure(over.RootElement, limits));
        Assert.Equal(RuntimeRpcValueLimitKind.ObjectMembers, exception.LimitKind);
    }

    [Fact]
    public void InlineDecodedBytes_FollowsRefsAndUnionsAndCountsConcreteValueOnce()
    {
        var contract = InlineDecodedContract();
        var fiveBytes = Convert.ToBase64String(new byte[5]);
        var value = JsonSerializer.SerializeToElement(new
        {
            primary = fiveBytes,
            choice = new { kind = "unknown", data = fiveBytes },
            representation = new
            {
                kind = "text",
                data = Convert.ToBase64String(new byte[100]),
            },
        });

        var measured = RuntimeRpcInlineDecodedMeasurer.Measure(
            contract,
            "#/$defs/Payload",
            value,
            maximumDecodedBytes: 10);
        var exhausted = Assert.Throws<RuntimeRpcValueLimitException>(() =>
            RuntimeRpcInlineDecodedMeasurer.Measure(
                contract,
                "#/$defs/Payload",
                value,
                maximumDecodedBytes: 9));

        Assert.Equal(10, measured);
        Assert.Equal(RuntimeRpcValueLimitKind.InlineDecodedBytes, exhausted.LimitKind);
    }

    private static RuntimeRpcPolicyOptions Limits(
        int bytes = 1024,
        int depth = 64,
        int nodes = 1024,
        int members = 1024)
        => new()
        {
            MaxPayloadBytes = bytes,
            MaxPayloadDepth = depth,
            MaxPayloadNodes = nodes,
            MaxObjectMembers = members,
        };

    private static SunderRpcContractDescriptor InlineDecodedContract()
        => SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "$schema":"https://json-schema.org/draft/2020-12/schema",
              "descriptorVersion":2,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"measurement.rpc",
              "version":"1.0.0",
              "errors":{},
              "services":{"values":{"methods":{
                "send":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Payload"},"responseSchema":{"$ref":"#/$defs/Payload"}}
              }}},
              "$defs":{
                "Bytes":{"type":"string","contentEncoding":"base64"},
                "BytesAlias":{"$ref":"#/$defs/Bytes"},
                "ChoiceOne":{"type":"object","properties":{"kind":{"type":"string","const":"one"},"data":{"$ref":"#/$defs/Bytes"}},"required":["kind","data"],"additionalProperties":false},
                "ChoiceTwo":{"type":"object","properties":{"kind":{"type":"string","const":"two"},"data":{"$ref":"#/$defs/BytesAlias"}},"required":["kind","data"],"additionalProperties":false},
                "Encoded":{"type":"object","properties":{"kind":{"type":"string","const":"encoded"},"data":{"$ref":"#/$defs/Bytes"}},"required":["kind","data"],"additionalProperties":false},
                "Text":{"type":"object","properties":{"kind":{"type":"string","const":"text"},"data":{"type":"string"}},"required":["kind","data"],"additionalProperties":false},
                "Payload":{"type":"object","properties":{"primary":{"$ref":"#/$defs/BytesAlias"},"choice":{"oneOf":[{"$ref":"#/$defs/ChoiceOne"},{"$ref":"#/$defs/ChoiceTwo"}]},"representation":{"oneOf":[{"$ref":"#/$defs/Encoded"},{"$ref":"#/$defs/Text"}]}},"required":["primary","choice","representation"],"additionalProperties":false}
              }
            }
            """));
}
