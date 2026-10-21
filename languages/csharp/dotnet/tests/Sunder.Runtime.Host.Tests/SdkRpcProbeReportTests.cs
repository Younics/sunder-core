using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class SdkRpcProbeReportTests
{
    [Theory]
    [InlineData("nonce", "stale")]
    [InlineData("unary", "wrong")]
    [InlineData("providerCancelled", "other-activation")]
    [InlineData("afterCancel", "wrong")]
    [InlineData("started", "false")]
    [InlineData("clientCancelled", "false")]
    [InlineData("stream", "missing-terminal-values")]
    public void ReportCannotSubstituteStaleOrIncompleteRpcBehavior(string field, string value)
    {
        var report = JsonNode.Parse("""
            {"protocol":"sunder.compatibility.probe.v1","nonce":"challenge","unary":"echo:challenge",
             "stream":["challenge:1","challenge:2"],"started":true,"clientCancelled":true,
             "providerCancelled":"challenge","afterCancel":"echo:after:challenge"}
            """)!;
        SdkRpcProbeReport.Validate(JsonSerializer.SerializeToElement(report), "challenge");
        report[field] = field is "started" or "clientCancelled" ? JsonValue.Create(false)
            : field == "stream" ? new JsonArray("challenge:1") : JsonValue.Create(value);
        Assert.Throws<InvalidDataException>(() => SdkRpcProbeReport.Validate(JsonSerializer.SerializeToElement(report), "challenge"));
    }
}
