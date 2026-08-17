using System.Text.Json;
using Sunder.Runtime.Host.Services;
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
    public void Utf8Bytes_UsesTransportEscapingAndPreservesPortableJsonNumbers()
    {
        using var document = JsonDocument.Parse(
            "{\"text\":\"é<&\\n\",\"numbers\":[1E+30,-0,1.2300]}");
        var expected = JsonSerializer.SerializeToUtf8Bytes(
            document.RootElement,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

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
            new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
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
}
