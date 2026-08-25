using System.Text;
using System.Text.Json;
using Sunder.Sdk.Storage;
using Sunder.Sdk.Worker.Internal;

namespace Sunder.Sdk.Worker.Tests;

public sealed class WorkerPackagePayloadCodecTests
{
    [Fact]
    public void PayloadStringTranslation_RequiresStrictUtf8AndPackageValueBounds()
    {
        Assert.Equal("stored value", WorkerPackagePayloadCodec.DecodeDataString("stored value"u8.ToArray()));

        var invalidUtf8 = Assert.Throws<WorkerProtocolException>(() =>
            WorkerPackagePayloadCodec.DecodeDataString([0xff]));
        Assert.Equal("Host package-data payload is not strict UTF-8.", invalidUtf8.Message);

        var oversized = Encoding.UTF8.GetBytes(
            new string('x', PackageStorageValidation.MaximumValueUtf8Bytes + 1));
        var tooLarge = Assert.Throws<WorkerProtocolException>(() =>
            WorkerPackagePayloadCodec.DecodeDataString(oversized));
        Assert.Equal("Host package-data payload exceeds the package value limit.", tooLarge.Message);
    }

    [Fact]
    public void PayloadKeyTranslation_RequiresStrictJsonAndCanonicalKeys()
    {
        Assert.Equal(
            ["cache.first", "cache.second"],
            WorkerPackagePayloadCodec.ReadPayloadKeys(
                "[\"cache.first\",\"cache.second\"]"u8.ToArray(),
                maximumDepth: 4));

        Assert.Throws<WorkerProtocolException>(() =>
            WorkerPackagePayloadCodec.ReadPayloadKeys("[\"cache.second\",\"cache.first\"]"u8.ToArray(), 4));
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerPackagePayloadCodec.ReadPayloadKeys("[\"cache.first\",\"cache.first\"]"u8.ToArray(), 4));
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerPackagePayloadCodec.ReadPayloadKeys("[\"invalid key\"]"u8.ToArray(), 4));
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerPackagePayloadCodec.ReadPayloadKeys("{\"key\":true}"u8.ToArray(), 4));
    }

    [Fact]
    public void DataEnvelopeTranslation_PreservesInlineAndPayloadShapes()
    {
        using var inlineDocument = JsonDocument.Parse("""
            {"kind":"inline","value":"hello"}
            """);
        var inline = WorkerPackagePayloadCodec.ReadDataValue(
            inlineDocument.RootElement,
            maximumPayloadBytes: 1024);
        Assert.True(inline.IsInline);
        Assert.Equal("hello", inline.Value);
        Assert.Null(inline.Payload);

        using var payloadDocument = JsonDocument.Parse("""
            {
              "kind":"payload",
              "handleId":"payload-1",
              "filePath":"payload-1.data",
              "length":5,
              "sha256":"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"
            }
            """);
        var payload = WorkerPackagePayloadCodec.ReadDataValue(
            payloadDocument.RootElement,
            maximumPayloadBytes: 1024);
        Assert.False(payload.IsInline);
        Assert.Null(payload.Value);
        Assert.Equal(
            new WorkerDataPayload(
                "payload-1",
                "payload-1.data",
                5,
                "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"),
            payload.Payload);
    }
}
