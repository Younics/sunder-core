using System.Text;
using Sunder.Sdk.Worker.Internal;
using Sunder.Sdk.Worker.Tests.TestSupport;

namespace Sunder.Sdk.Worker.Tests;

public sealed class WorkerFramingTests
{
    [Fact]
    public async Task Reader_AcceptsExactContentLengthAndFragmentedInput()
    {
        using var stream = new AsyncByteStream();
        var reader = new WorkerFrameReader(stream, WorkerLimits.Default);
        var frame = "{\"type\":\"host.activate\",\"sessionGeneration\":7}"u8.ToArray();
        var wire = Encoding.ASCII.GetBytes($"Content-Length: {frame.Length}\r\n\r\n")
            .Concat(frame)
            .ToArray();
        foreach (var value in wire)
        {
            await stream.WriteAsync(new[] { value });
        }

        using var document = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal("host.activate", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(7, document.RootElement.GetProperty("sessionGeneration").GetInt32());
    }

    [Theory]
    [InlineData("content-length: 2\r\n\r\n")]
    [InlineData("Content-Length: 02\r\n\r\n")]
    [InlineData("Content-Length: 2\r\nX-Test: x\r\n\r\n")]
    [InlineData("Content-Length: 0\r\n\r\n")]
    public async Task Reader_RejectsNonCanonicalHeaders(string header)
    {
        using var stream = new AsyncByteStream();
        var reader = new WorkerFrameReader(stream, WorkerLimits.Default);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync("{}"u8.ToArray());

        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await reader.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"type\":\"host.activate\",\"type\":\"host.shutdown\"}")]
    [InlineData("{\"type\":\"host.activate\",\"Type\":\"host.shutdown\"}")]
    [InlineData("{\"type\":\"host.activate\",\"value\":{\"a\":1,\"A\":2}}")]
    public async Task Reader_RejectsDuplicateAndCaseCollidingProperties(string json)
    {
        using var stream = new AsyncByteStream();
        var reader = new WorkerFrameReader(stream, WorkerLimits.Default);
        var body = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        await stream.WriteAsync(body);

        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reader_RejectsBomInvalidUtf8AndExcessDepth()
    {
        await AssertRejectedAsync([0xef, 0xbb, 0xbf, (byte)'{', (byte)'}'], WorkerLimits.Default);
        await AssertRejectedAsync([0xff], WorkerLimits.Default);
        await AssertRejectedAsync(
            "{\"a\":{\"b\":{}}}"u8.ToArray(),
            WorkerLimits.Default with { MaximumMessageDepth = 2 });
    }

    private static async Task AssertRejectedAsync(byte[] body, WorkerLimits limits)
    {
        using var stream = new AsyncByteStream();
        var reader = new WorkerFrameReader(stream, limits);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        await stream.WriteAsync(body);
        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await reader.ReadAsync(CancellationToken.None));
    }
}
