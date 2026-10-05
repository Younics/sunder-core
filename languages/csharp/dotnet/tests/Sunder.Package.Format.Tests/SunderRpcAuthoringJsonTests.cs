using System.Text;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class SunderRpcAuthoringJsonTests
{
    [Fact]
    public void NullableRegistryMetadataCannotBeAdoptedAsCanonicalAuthoringEvidence()
    {
        var bytes = "{\"version\":\"1.0.0\",\"deprecatedMessage\":null}"u8.ToArray();
        using var metadata = SunderRpcAuthoringJson.ParseRegistryMetadata(bytes, 1024);
        Assert.Equal(JsonValueKind.Null, metadata.RootElement.GetProperty("deprecatedMessage").ValueKind);
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringJson.Parse(bytes, 1024));
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringJson.WriteCompact(metadata.RootElement, 1024));
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringJson.ParseRegistryMetadata("{\"name\":null,\"Name\":null}"u8, 1024));
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringJson.ParseRegistryMetadata(bytes, bytes.Length - 1));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("0")]
    [InlineData("\"value\"")]
    public void ReaderAndWriter_AgreeOnTheExactContainerDepthBoundary(string leaf)
    {
        var isContainer = leaf is "{}" or "[]";
        var prefixDepth = isContainer ? 15 : 16;
        var allowed = new string('[', prefixDepth) + leaf + new string(']', prefixDepth);
        using var parsed = SunderRpcAuthoringJson.Parse(Encoding.UTF8.GetBytes(allowed), 1024);
        Assert.Equal(allowed, Encoding.UTF8.GetString(SunderRpcAuthoringJson.WriteCompact(parsed.RootElement, 1024)));

        var excessive = "[" + allowed + "]";
        using var uncheckedDocument = JsonDocument.Parse(excessive);
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringJson.WriteCompact(uncheckedDocument.RootElement, 1024));
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringJson.Parse(Encoding.UTF8.GetBytes(excessive), 1024));
    }

    [Fact]
    public void CompactWriter_UsesExactEscapingAndCountsUtf8BytesBeforeWriting()
    {
        var value = JsonSerializer.SerializeToElement(new { text = "</>&\u2028\ud83d\udca1\n\u0001\"\\" });
        var expected = Encoding.UTF8.GetBytes("{\"text\":\"</>&\u2028\ud83d\udca1\\n\\u0001\\\"\\\\\"}");
        Assert.Equal(expected, SunderRpcAuthoringJson.WriteCompact(value, expected.Length));
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringJson.WriteCompact(value, expected.Length - 1));
        using var parsed = SunderRpcAuthoringJson.Parse(expected, expected.Length);
        Assert.Equal(expected, SunderRpcAuthoringJson.WriteCompact(parsed.RootElement, expected.Length));
    }

    [Theory]
    [InlineData("-0")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("9007199254740992")]
    [InlineData("null")]
    [InlineData("{\"name\":1,\"Name\":2}")]
    [InlineData("{\"name\":1,\"na\\u006de\":2}")]
    [InlineData("\"\\ud800\"")]
    public void Parse_RejectsNoncanonicalOrAmbiguousRepresentations(string json)
        => Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringJson.Parse(Encoding.UTF8.GetBytes(json), 1024));

    [Fact]
    public void StructuralBudget_IsEnforcedBeforeDocumentAllocationAndByTheWriter()
    {
        var allowed = "[" + string.Join(',', Enumerable.Repeat("0", 262_143)) + "]";
        using var parsed = SunderRpcAuthoringJson.Parse(Encoding.UTF8.GetBytes(allowed), 1_048_576);
        Assert.Equal(allowed, Encoding.UTF8.GetString(SunderRpcAuthoringJson.WriteCompact(parsed.RootElement, 1_048_576)));

        var excessive = allowed[..^1] + ",0]";
        var failure = Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringJson.Parse(Encoding.UTF8.GetBytes(excessive), 1_048_576));
        Assert.Contains("structural", failure.Message, StringComparison.Ordinal);
        using var uncheckedDocument = JsonDocument.Parse(excessive);
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringJson.WriteCompact(uncheckedDocument.RootElement, 1_048_576));
    }
}
