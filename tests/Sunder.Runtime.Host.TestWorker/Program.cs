using System.Text;
using System.Text.Json;

var modePath = Path.Combine(Environment.CurrentDirectory, "mode.txt");
var mode = File.Exists(modePath) ? File.ReadAllText(modePath).Trim() : "normal";
var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var error = Console.Error;

var hello = await ReadFrameAsync(input);
if (hello.RootElement.GetProperty("type").GetString() != "host.hello") return 64;
if (mode == "startup-hang")
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}
if (mode == "stderr")
{
    await error.WriteLineAsync(new string('x', 32 * 1024));
    await error.FlushAsync();
}

await WriteFrameAsync(output, new
{
    type = "worker.ready",
    protocol = hello.RootElement.GetProperty("protocol").GetString(),
    protocolVersion = hello.RootElement.GetProperty("protocolVersion").GetInt32(),
    challenge = hello.RootElement.GetProperty("challenge").GetString(),
    packageId = hello.RootElement.GetProperty("packageId").GetString(),
    packageVersion = hello.RootElement.GetProperty("packageVersion").GetString(),
    activationId = hello.RootElement.GetProperty("activationId").GetString(),
    sessionId = hello.RootElement.GetProperty("sessionId").GetString(),
    providers = hello.RootElement.GetProperty("providers"),
});

while (true)
{
    using var frame = await ReadFrameAsync(input);
    var root = frame.RootElement;
    var type = root.GetProperty("type").GetString();
    if (type == "host.activate")
    {
        if (mode == "crash") return 42;
        if (mode == "activation-hang") continue;
        if (mode == "malformed-frame")
        {
            await output.WriteAsync("Content-Length: nope\r\n\r\n"u8.ToArray());
            await output.FlushAsync();
            continue;
        }
        await WriteFrameAsync(output, new
        {
            type = "worker.activated",
            sessionGeneration = root.GetProperty("sessionGeneration").GetInt64(),
        });
        if (mode == "caller-spoof")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.discover",
                id = "spoof1",
                contractId = "example.rpc",
                caller = new { packageId = "spoofed.package" },
            });
        }
        if (mode == "content-spoof")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.content-open",
                id = "contentSpoof1",
                invocationId = "notAHostInvocation",
                reference = new
                {
                    id = "rpc-content-spoof",
                    length = 0,
                    sha256 = new string('0', 64),
                    mediaType = "application/octet-stream",
                    fileName = "spoof.bin",
                    expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                    repeatability = "single-use",
                },
            });
        }
        continue;
    }
    if (type == "host.invoke")
    {
        var id = root.GetProperty("id").GetString();
        var kind = root.GetProperty("kind").GetString();
        var method = root.GetProperty("methodId").GetString();
        if (method == "wait") continue;
        if (method == "content-roundtrip")
        {
            var invocationId = id!;
            var request = root.GetProperty("request");
            await WriteFrameAsync(output, new
            {
                type = "worker.content-open",
                id = invocationId + "open",
                invocationId,
                reference = request.GetProperty("content"),
            });
            using var openResponse = await ReadFrameAsync(input);
            var opened = openResponse.RootElement.GetProperty("value");
            var handleId = opened.GetProperty("handleId").GetString()!;
            var openedPath = opened.GetProperty("filePath").GetString()!;
            var content = await File.ReadAllTextAsync(openedPath);
            var outputDirectory = Path.Combine(
                Environment.GetEnvironmentVariable("SUNDER_PACKAGE_DATA_PATH")!,
                "tmp");
            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, invocationId + ".txt");
            var outputContent = content + "-processed";
            await File.WriteAllTextAsync(outputPath, outputContent);
            await WriteFrameAsync(output, new
            {
                type = "worker.content-register",
                id = invocationId + "register",
                invocationId,
                filePath = outputPath,
                options = new
                {
                    mediaType = "text/plain",
                    fileName = "processed.txt",
                    length = Encoding.UTF8.GetByteCount(outputContent),
                    expiresAtUtc = (string?)null,
                    repeatability = "single-use",
                    maximumUses = 1,
                },
            });
            using var registerResponse = await ReadFrameAsync(input);
            var registered = registerResponse.RootElement.GetProperty("value").Clone();
            await WriteFrameAsync(output, new
            {
                type = "worker.content-discard",
                id = invocationId + "discard",
                invocationId,
                handleId,
            });
            using var discardResponse = await ReadFrameAsync(input);
            _ = discardResponse.RootElement.GetProperty("value");
            await WriteFrameAsync(output, new
            {
                type = "worker.result",
                id,
                value = new
                {
                    accepted = true,
                    content = registered,
                    openedFileDiscarded = !File.Exists(openedPath),
                },
            });
            continue;
        }
        if (kind == "server-stream")
        {
            await WriteFrameAsync(output, new { type = "worker.event", id, value = new { accepted = true, sequence = 1 } });
            await WriteFrameAsync(output, new { type = "worker.event", id, value = new { accepted = true, sequence = 2 } });
            await WriteFrameAsync(output, new { type = "worker.complete", id });
        }
        else
        {
            if (mode == "handler-fault")
            {
                await WriteFrameAsync(output, new
                {
                    type = "worker.error",
                    id,
                    error = new { kind = "provider-fault", code = "rpc.provider.handler-fault", message = "handler failed" },
                });
                continue;
            }
            if (mode == "forwarded-error")
            {
                await WriteFrameAsync(output, new
                {
                    type = "worker.error",
                    id,
                    error = new { kind = "permission-denied", code = "rpc.permission.denied", message = "permission denied" },
                });
                continue;
            }
            object value = mode switch
            {
                "invalid-output" => new { accepted = "invalid" },
                "environment" => new
                {
                    accepted = true,
                    nodeOptions = Environment.GetEnvironmentVariable("NODE_OPTIONS"),
                    runtimeToken = Environment.GetEnvironmentVariable("SUNDER_RUNTIME_BEARER_TOKEN"),
                    packageId = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_ID"),
                    packageVersion = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_VERSION"),
                    activationId = Environment.GetEnvironmentVariable("SUNDER_ACTIVATION_ID"),
                    sessionId = Environment.GetEnvironmentVariable("SUNDER_SESSION_ID"),
                    dataPath = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_DATA_PATH"),
                    statePath = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_STATE_PATH"),
                    workingPath = Environment.CurrentDirectory,
                    deadlineUtc = root.GetProperty("context").GetProperty("deadlineUtc").GetString(),
                },
                _ => new { accepted = true },
            };
            await WriteFrameAsync(output, new { type = "worker.result", id, value });
            if (mode == "duplicate-terminal")
            {
                await WriteFrameAsync(output, new { type = "worker.result", id, value });
            }
        }
        continue;
    }
    if (type == "host.cancel")
    {
        await WriteFrameAsync(output, new
        {
            type = "worker.error",
            id = root.GetProperty("id").GetString(),
            error = new { kind = "cancelled", code = "rpc.call.cancelled", message = "cancelled" },
        });
        continue;
    }
    if (type == "host.shutdown")
    {
        if (mode == "shutdown-hang")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        await WriteFrameAsync(output, new
        {
            type = "worker.shutdown-ack",
            shutdownId = root.GetProperty("shutdownId").GetString(),
        });
        return 0;
    }
}

static async Task<JsonDocument> ReadFrameAsync(Stream input)
{
    var header = new List<byte>();
    while (header.Count < 8192)
    {
        var value = input.ReadByte();
        if (value < 0) throw new EndOfStreamException();
        header.Add((byte)value);
        if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n') break;
    }
    var text = Encoding.ASCII.GetString([.. header]);
    const string prefix = "Content-Length: ";
    var length = int.Parse(text[prefix.Length..^4]);
    var body = new byte[length];
    await input.ReadExactlyAsync(body);
    return JsonDocument.Parse(body);
}

static async Task WriteFrameAsync(Stream output, object value)
{
    var body = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    });
    var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
    await output.WriteAsync(header);
    await output.WriteAsync(body);
    await output.FlushAsync();
}
