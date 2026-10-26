using System.Text.Json;

internal sealed class CallOwnershipProbe
{
    private string? _watchId;
    private DateTimeOffset _watchDeadline;

    internal async Task HandleAsync(JsonElement request, Func<Task<JsonDocument>> read,
        Func<object, Task> write, Func<string, object, Task> status)
    {
        var id = request.GetProperty("id").GetString();
        var context = request.GetProperty("context");
        if (request.GetProperty("kind").GetString() == "server-stream")
        {
            _watchId = id;
            _watchDeadline = context.GetProperty("deadlineUtc").GetDateTimeOffset();
            await write(new { type = "worker.event", id, value = new { accepted = true } });
            return;
        }

        await Send("named", id);
        using var named = await Next("host.invoke");
        await Send("nested", named.RootElement.GetProperty("id").GetString());
        using var nested = await Next("host.invoke");
        await Send("sibling", id);
        using var sibling = await Next("host.invoke");
        await Send("root", null);
        using var root = await Next("host.invoke");
        await status("call-ownership.json", new
        {
            namedDeadline = named.RootElement.GetProperty("context").GetProperty("deadlineUtc").GetString(),
            rootDeadline = root.RootElement.GetProperty("context").GetProperty("deadlineUtc").GetString(),
            namedDepth = named.RootElement.GetProperty("context").GetProperty("callDepth").GetInt32(),
            nestedDepth = nested.RootElement.GetProperty("context").GetProperty("callDepth").GetInt32(),
            siblingDepth = sibling.RootElement.GetProperty("context").GetProperty("callDepth").GetInt32(),
            rootDepth = root.RootElement.GetProperty("context").GetProperty("callDepth").GetInt32(),
            lineageEnabled = Environment.GetEnvironmentVariable("SUNDER_RPC_EXTENDED_DEADLINE_V1"),
        });
        var remaining = _watchDeadline - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        await write(new { type = "worker.result", id = root.RootElement.GetProperty("id").GetString(), value = new { accepted = true } });
        using var rootResult = await Next(null);
        await write(new { type = "worker.result", id = sibling.RootElement.GetProperty("id").GetString(), value = new { accepted = true } });
        using var siblingResult = await Next(null);
        await write(new { type = "worker.result", id = nested.RootElement.GetProperty("id").GetString(), value = new { accepted = true } });
        using var nestedResult = await Next(null);
        await write(new { type = "worker.result", id = named.RootElement.GetProperty("id").GetString(), value = new { accepted = true } });
        using var namedResult = await Next(null);
        await write(new { type = "worker.result", id, value = new
        {
            accepted = rootResult.RootElement.GetProperty("type").GetString() == "host.result"
                && siblingResult.RootElement.GetProperty("type").GetString() == "host.result"
                && nestedResult.RootElement.GetProperty("type").GetString() == "host.result"
                && namedResult.RootElement.GetProperty("type").GetString() == "host.result",
        } });

        Task Send(string call, string? parent) => write(new
        {
            type = "worker.invoke", id = call,
            endpointReference = context.GetProperty("provider").GetProperty("endpointReference").GetString(),
            serviceId = "messages", methodId = "send", request = new { message = call },
            deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"), parentInvocationId = parent,
        });

        async Task<JsonDocument> Next(string? expected)
        {
            while (true)
            {
                var frame = await read();
                var type = frame.RootElement.GetProperty("type").GetString();
                if (type == "host.cancel")
                {
                    if (frame.RootElement.GetProperty("id").GetString() == _watchId)
                        await write(new { type = "worker.complete", id = _watchId });
                    frame.Dispose();
                    continue;
                }
                if (expected is not null && type != expected) throw new InvalidDataException("Unexpected ownership probe frame: " + type);
                return frame;
            }
        }
    }
}
