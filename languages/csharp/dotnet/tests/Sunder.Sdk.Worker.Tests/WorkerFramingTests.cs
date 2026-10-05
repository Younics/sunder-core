using System.Text;
using System.Text.Json;
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
    public async Task Reader_UsesAsciiOnlyCollisionFoldingForNestedRpcValues()
    {
        using var stream = new AsyncByteStream();
        var reader = new WorkerFrameReader(stream, WorkerLimits.Default);
        var body = "{\"type\":\"host.result\",\"id\":\"unicode1\",\"value\":{\"k\":true,\"\u212a\":false,\"\u00e9\":1,\"\u00c9\":2}}"u8.ToArray();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        await stream.WriteAsync(body);

        using var document = await reader.ReadAsync(CancellationToken.None);

        var value = document.RootElement.GetProperty("value");
        Assert.Equal(4, value.EnumerateObject().Count());
        Assert.True(value.GetProperty("k").GetBoolean());
        Assert.False(value.GetProperty("\u212a").GetBoolean());
    }

    [Fact]
    public void Writer_UsesAsciiOnlyCollisionFoldingForNestedRpcValues()
    {
        using var source = JsonDocument.Parse("{\"k\":true,\"\u212a\":false,\"\u00e9\":1,\"\u00c9\":2}");

        var body = WorkerJson.Serialize(
            WorkerLimits.Default,
            writer => WorkerJson.WriteJsonValue(writer, source.RootElement));
        using var output = JsonDocument.Parse(body);

        Assert.Equal(4, output.RootElement.EnumerateObject().Count());
        Assert.True(output.RootElement.GetProperty("k").GetBoolean());
        Assert.False(output.RootElement.GetProperty("\u212a").GetBoolean());
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

    [Fact]
    public async Task ReaderAndWriter_EnforceNegotiatedValueNodeAndObjectMemberLimits()
    {
        var limits = WorkerLimits.Default with
        {
            MaximumValueNodes = 3,
            MaximumObjectMembers = 1,
        };

        await AssertRejectedAsync(
            "{\"type\":\"host.result\",\"value\":[null,null,null]}"u8.ToArray(),
            limits);
        await AssertRejectedAsync(
            "{\"type\":\"host.result\",\"value\":{\"a\":1,\"b\":2}}"u8.ToArray(),
            limits);

        using var exactNodes = JsonDocument.Parse("[null,null]");
        _ = SerializeResultEnvelope(limits, exactNodes.RootElement, string.Empty);
        using var excessiveNodes = JsonDocument.Parse("[null,null,null]");
        Assert.Throws<WorkerTransportResourceException>(() =>
            SerializeResultEnvelope(limits, excessiveNodes.RootElement, string.Empty));
        using var excessiveMembers = JsonDocument.Parse("{\"a\":1,\"b\":2}");
        Assert.Throws<WorkerTransportResourceException>(() =>
            SerializeResultEnvelope(limits, excessiveMembers.RootElement, string.Empty));
    }

    [Fact]
    [Trait("Category", "Conformance")]
    [Trait("Size", "Small")]
    public async Task Reader_ExecutesSharedCrossLanguageTransportVectors()
        => await VerifySharedTransportVectorsAsync(mutateFirstExpectation: false);

    [Fact]
    [Trait("Category", "Conformance")]
    [Trait("Size", "Small")]
    public async Task Reader_SharedTransportOracleRejectsSeededExpectationMutation()
        => await Assert.ThrowsAnyAsync<Exception>(() => VerifySharedTransportVectorsAsync(mutateFirstExpectation: true));

    [Fact]
    public void Writer_AccountsForExactEnvelopeMetadataEscapingAndValueDepth()
    {
        var limits = WorkerLimits.Default with
        {
            MaximumRpcValueBytes = 4096,
            MaximumValueDepth = 3,
            MaximumFrameBytes = 4096 + WorkerProtocol.EnvelopeReserveBytes,
            MaximumMessageDepth = 11,
        };
        var escapedText = "snowman \u2603 \" \\ \u00e9";
        var value = JsonSerializer.SerializeToElement(new { text = escapedText });
        var valueBytes = WorkerJson.GetSerializedValueByteCount(value, limits);
        var baseline = SerializeResultEnvelope(limits, value, string.Empty);
        var metadataBytes = baseline.Length - valueBytes;
        var padding = new string('m', WorkerProtocol.EnvelopeReserveBytes - metadataBytes);

        var exact = SerializeResultEnvelope(limits, value, padding);

        Assert.Equal(valueBytes + WorkerProtocol.EnvelopeReserveBytes, exact.Length);
        var encodedText = JsonSerializer.Serialize(
            escapedText,
            new JsonSerializerOptions { Encoder = WorkerJson.Encoder });
        Assert.Contains(encodedText, Encoding.UTF8.GetString(exact), StringComparison.Ordinal);
        Assert.Throws<WorkerTransportResourceException>(() =>
            SerializeResultEnvelope(limits, value, padding + "m"));

        using var exactDepth = JsonDocument.Parse("[[null]]");
        _ = SerializeResultEnvelope(limits, exactDepth.RootElement, string.Empty);
        using var excessiveDepth = JsonDocument.Parse("[[[null]]]");
        Assert.Throws<WorkerTransportResourceException>(() =>
            SerializeResultEnvelope(limits, excessiveDepth.RootElement, string.Empty));

        var errorBaseline = SerializeErrorEnvelope(limits, value, string.Empty);
        var errorMetadataBytes = errorBaseline.Length - valueBytes;
        var errorPadding = new string('m', WorkerProtocol.EnvelopeReserveBytes - errorMetadataBytes);
        var exactError = SerializeErrorEnvelope(limits, value, errorPadding);
        Assert.Equal(valueBytes + WorkerProtocol.EnvelopeReserveBytes, exactError.Length);
        Assert.Throws<WorkerTransportResourceException>(() =>
            SerializeErrorEnvelope(limits, value, errorPadding + "m"));
        _ = SerializeErrorEnvelope(limits, exactDepth.RootElement, string.Empty);
        Assert.Throws<WorkerTransportResourceException>(() =>
            SerializeErrorEnvelope(limits, excessiveDepth.RootElement, string.Empty));
    }

    [Fact]
    public void EventQueueValueClone_CompactsLexicalPaddingBeforeByteAccounting()
    {
        var padding = new string(' ', 4096);
        using var source = JsonDocument.Parse($"{{{padding}\"value\"{padding}:{padding}1{padding}}}");

        var queued = WorkerJson.CloneValueForQueue(
            source.RootElement,
            WorkerLimits.Default,
            out var serializedBytes);

        Assert.True(Encoding.UTF8.GetByteCount(source.RootElement.GetRawText()) > serializedBytes);
        Assert.Equal(serializedBytes, Encoding.UTF8.GetByteCount(queued.GetRawText()));
        Assert.Equal(1, queued.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task WriteAndEventQueues_ReleaseByteReservationsAfterDequeue()
    {
        var limits = WorkerLimits.Default with
        {
            MaximumWriteQueueMessages = 10,
            MaximumWriteQueueBytes = 8,
        };
        using var stream = new BlockingWriteStream();
        using var stopping = new CancellationTokenSource();
        var output = new WorkerProtocolOutput(stream, limits, stopping.Token);
        var body = new byte[8];
        var first = output.WriteAsync(body).AsTask();
        await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));
        var second = output.WriteAsync(body).AsTask();

        Assert.Throws<WorkerTransportResourceException>(() => output.WriteAsync(body));

        stream.Release();
        await Task.WhenAll(first, second);
        await output.WriteAsync(body);

        var events = new WorkerJsonEventQueue(maximumMessages: 1, maximumBytes: 8);
        using var one = JsonDocument.Parse("1");
        using var two = JsonDocument.Parse("2");
        Assert.Null(events.TryWrite(one.RootElement.Clone(), 8));
        var rejection = events.TryWrite(two.RootElement.Clone(), 1);
        Assert.True(rejection.HasValue);
        Assert.Equal(
            WorkerJsonEventQueueLimit.Count | WorkerJsonEventQueueLimit.Bytes,
            rejection.Value.Limit);
        Assert.Equal(1, rejection.Value.QueuedMessages);
        Assert.Equal(8, rejection.Value.QueuedBytes);
        Assert.Equal(1, rejection.Value.MaximumMessages);
        Assert.Equal(8, rejection.Value.MaximumBytes);
        await using var reader = events.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Null(events.TryWrite(two.RootElement.Clone(), 8));
        events.Complete();
    }

    [Fact]
    public void EventQueue_RejectionSnapshotClassifiesCountBytesAndBoth()
    {
        using var value = JsonDocument.Parse("1");

        var countQueue = new WorkerJsonEventQueue(maximumMessages: 1, maximumBytes: 8);
        Assert.Null(countQueue.TryWrite(value.RootElement.Clone(), 4));
        var count = countQueue.TryWrite(value.RootElement.Clone(), 4);
        Assert.True(count.HasValue);
        Assert.Equal(WorkerJsonEventQueueLimit.Count, count.Value.Limit);
        Assert.Equal(1, count.Value.QueuedMessages);
        Assert.Equal(4, count.Value.QueuedBytes);
        Assert.Equal(1, count.Value.MaximumMessages);
        Assert.Equal(8, count.Value.MaximumBytes);
        Assert.Equal(4, count.Value.IncomingBytes);

        var byteQueue = new WorkerJsonEventQueue(maximumMessages: 2, maximumBytes: 8);
        Assert.Null(byteQueue.TryWrite(value.RootElement.Clone(), 5));
        var bytes = byteQueue.TryWrite(value.RootElement.Clone(), 4);
        Assert.True(bytes.HasValue);
        Assert.Equal(WorkerJsonEventQueueLimit.Bytes, bytes.Value.Limit);
        Assert.Equal(1, bytes.Value.QueuedMessages);
        Assert.Equal(5, bytes.Value.QueuedBytes);

        var bothQueue = new WorkerJsonEventQueue(maximumMessages: 1, maximumBytes: 8);
        Assert.Null(bothQueue.TryWrite(value.RootElement.Clone(), 8));
        var both = bothQueue.TryWrite(value.RootElement.Clone(), 1);
        Assert.True(both.HasValue);
        Assert.Equal(
            WorkerJsonEventQueueLimit.Count | WorkerJsonEventQueueLimit.Bytes,
            both.Value.Limit);
        Assert.Equal(1, both.Value.QueuedMessages);
        Assert.Equal(8, both.Value.QueuedBytes);
    }

    [Fact]
    public async Task EventQueue_FailCanRaceTheNormalConsumer()
    {
        using var value = JsonDocument.Parse("1");
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var queue = new WorkerJsonEventQueue(maximumMessages: 64, maximumBytes: 64);
            for (var index = 0; index < 64; index++)
            {
                Assert.Null(queue.TryWrite(value.RootElement.Clone(), 1));
            }

            var failure = new InvalidOperationException($"expected-{attempt}");
            var consumer = Task.Run(async () =>
            {
                var observed = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    await foreach (var _ in queue.ReadAllAsync(CancellationToken.None))
                    {
                        await Task.Yield();
                    }
                });
                Assert.Same(failure, observed);
            });

            await Task.Yield();
            queue.Fail(failure);
            await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static byte[] SerializeResultEnvelope(
        WorkerLimits limits,
        JsonElement value,
        string metadata)
        => WorkerJson.Serialize(limits, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "worker.result");
            writer.WriteString("id", "escape");
            writer.WriteString("metadata", metadata);
            writer.WritePropertyName("value");
            WorkerJson.WriteJsonValue(writer, value);
            writer.WriteEndObject();
        });

    private static byte[] SerializeErrorEnvelope(
        WorkerLimits limits,
        JsonElement details,
        string metadata)
        => WorkerJson.Serialize(limits, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "worker.error");
            writer.WriteString("id", "escape");
            writer.WriteString("metadata", metadata);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("kind", "domain");
            writer.WriteString("code", "example.rejected");
            writer.WriteString("message", "Rejected.");
            writer.WritePropertyName("details");
            WorkerJson.WriteJsonValue(writer, details);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    private static async Task AssertRejectedAsync(byte[] body, WorkerLimits limits)
    {
        using var stream = new AsyncByteStream();
        var reader = new WorkerFrameReader(stream, limits);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        await stream.WriteAsync(body);
        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await reader.ReadAsync(CancellationToken.None));
    }

    private static async Task VerifySharedTransportVectorsAsync(bool mutateFirstExpectation)
    {
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "WorkerV1Conformance",
            "transport-vectors.json")));
        Assert.Equal(1, corpus.RootElement.GetProperty("formatVersion").GetInt32());
        var index = 0;
        foreach (var vector in corpus.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var body = vector.TryGetProperty("bodyBase64", out var base64)
                ? Convert.FromBase64String(base64.GetString()!)
                : Encoding.UTF8.GetBytes(vector.GetProperty("bodyUtf8").GetString()!);
            var header = vector.GetProperty("header").GetString()!
                .Replace("{{length}}", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var limits = WorkerLimits.Default with
            {
                MaximumFrameBytes = vector.TryGetProperty("maxFrameBytes", out var frameBytes)
                    ? frameBytes.GetInt32()
                    : WorkerLimits.Default.MaximumFrameBytes,
                MaximumMessageDepth = vector.TryGetProperty("maxMessageDepth", out var messageDepth)
                    ? messageDepth.GetInt32()
                    : WorkerLimits.Default.MaximumMessageDepth,
            };
            using var stream = new AsyncByteStream();
            var reader = new WorkerFrameReader(stream, limits);
            foreach (var value in Encoding.ASCII.GetBytes(header).Concat(body))
            {
                await stream.WriteAsync(new byte[] { value });
            }
            var expected = vector.GetProperty("valid").GetBoolean();
            if (mutateFirstExpectation && index == 0) expected = !expected;

            if (expected)
            {
                using var document = await reader.ReadAsync(CancellationToken.None);
                Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            }
            else
            {
                var exception = await Record.ExceptionAsync(async () =>
                    await reader.ReadAsync(CancellationToken.None));
                Assert.True(
                    exception is WorkerProtocolException,
                    $"Transport vector '{name}' was accepted or failed with the wrong exception: {exception}");
            }
            index++;
        }
        Assert.True(index >= 10, "The shared Worker transport corpus lost boundary vectors.");
    }

    private sealed class BlockingWriteStream : Stream
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Release() => _released.TrySetResult();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
        }
    }
}
