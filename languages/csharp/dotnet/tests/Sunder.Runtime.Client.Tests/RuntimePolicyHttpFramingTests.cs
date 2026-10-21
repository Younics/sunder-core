using System.Diagnostics;
using System.Text;
using Xunit;

namespace Sunder.Runtime.Client.Tests;

public sealed class RuntimePolicyHttpFramingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundedLocalPolicyResponsesSupportLengthAndChunkedFraming(bool chunked)
    {
        var wire = chunked ? "Transfer-Encoding: chunked\r\n\r\n1\r\n{\r\n1;bounded=extension\r\n}\r\n0\r\nX-Trace: ignored\r\n\r\n"
            : "Content-Length: 2\r\n\r\n{}";
        using var response = Parse(wire, 2);
        using var stream = response.Content.ReadAsStream();
        Assert.Equal("{}", new StreamReader(stream).ReadToEnd());
        Assert.Equal(2, response.Content.Headers.ContentLength);
        Assert.Empty(response.Headers.TransferEncoding);
    }

    [Theory]
    [InlineData("Content-Length: 3\r\n\r\n{}", 8)]
    [InlineData("Content-Length: 9\r\n\r\n{}", 8)]
    [InlineData("Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}", 8)]
    [InlineData("Content-Length: 2\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n", 8)]
    [InlineData("Transfer-Encoding: gzip\r\n\r\n{}", 8)]
    [InlineData("Transfer-Encoding: chunked\r\n\r\n9\r\n", 8)]
    [InlineData("Transfer-Encoding: chunked\r\n\r\n1\r\n{bad\r\n0\r\n\r\n", 8)]
    [InlineData("Content-Length : 2\r\n\r\n{}", 8)]
    public void InvalidTruncatedOrOversizedFramingCannotReachPolicyJson(string wire, long maximum)
    {
        var error = Record.Exception(() => { using var response = Parse(wire, maximum); });
        Assert.True(error is InvalidDataException or EndOfStreamException, error?.ToString());
    }

    [Fact]
    public void HeadersAndUnknownLengthBodiesRemainBounded()
    {
        Assert.Throws<InvalidDataException>(() => Parse("X-Fill: " + new string('a', 32768) + "\r\n\r\n{}", 8));
        Assert.Throws<InvalidDataException>(() => Parse("\r\n" + new string('a', 9), 8));
    }

    private static HttpResponseMessage Parse(string wire, long maximum)
    {
        using var source = new MemoryStream(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n" + wire));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/api/handshake");
        return RuntimePolicyHttpMessageHandler.ReadResponse(source, request, maximum, Stopwatch.GetTimestamp(), default);
    }
}
