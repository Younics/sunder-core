using System.Globalization;
using System.Text.Json;

internal static class CallNestingProbe
{
    internal static async Task HandleAsync(JsonElement request, Func<Task<JsonDocument>> read,
        Func<object, Task> write, Func<string, object, Task> status)
    {
        var rootId = request.GetProperty("id").GetString()!;
        var context = request.GetProperty("context");
        var message = request.GetProperty("request").GetProperty("message").GetString();
        if (message == "health")
        {
            await status("call-nesting-health.json", new { depth = Depth(request) });
            await write(new { type = "worker.result", id = rootId,
                value = new { accepted = true } });
            return;
        }

        var limit = int.Parse(message!, CultureInfo.InvariantCulture);
        var watches = new List<(string HostId, string WorkerId)>();
        // Holding independent observations used to manufacture a deeper process
        // floor on every subscription, even though each one explicitly names no parent.
        for (var index = 0; index < 12; index++)
        {
            var workerId = $"watch{index}";
            await Send(workerId, null, subscribe: true);
            using var watch = await Next("host.invoke", expectedId: null);
            if (Depth(watch.RootElement) != 1) throw new InvalidDataException("An independent watch inherited another call's depth.");
            var hostId = watch.RootElement.GetProperty("id").GetString()!;
            await write(new { type = "worker.event", id = hostId, value = new { accepted = true } });
            using var connected = await Next("host.event", workerId);
            watches.Add((hostId, workerId));
        }

        var calls = new List<(string HostId, string WorkerId)>();
        var parent = rootId;
        for (var depth = 2; depth <= limit; depth++)
        {
            var workerId = $"child{depth}";
            await Send(workerId, parent);
            using var child = await Next("host.invoke", expectedId: null);
            if (Depth(child.RootElement) != depth) throw new InvalidDataException("A named child lost its nesting depth.");
            parent = child.RootElement.GetProperty("id").GetString()!;
            calls.Add((parent, workerId));
        }

        await Send("tooDeep", parent);
        using var rejected = await Next("host.error", "tooDeep");
        await write(new { type = "worker.scope-open", id = "openScope", parentInvocationId = parent,
            deadlineUtc = context.GetProperty("deadlineUtc").GetString() });
        using var opened = await Next("host.result", "openScope");
        var scopeId = opened.RootElement.GetProperty("value").GetProperty("scopeId").GetString();
        // Declaring a root cannot erase ancestry retained by an existing scope.
        await Send("scopedTooDeep", null, scopeId: scopeId);
        using var scopedRejected = await Next("host.error", "scopedTooDeep");
        await write(new { type = "worker.scope-close", id = "closeScope", scopeId });
        using var closed = await Next("host.result", "closeScope");

        await Send("sibling", rootId);
        using var sibling = await Next("host.invoke", expectedId: null);
        await Send("independent", null);
        using var independent = await Next("host.invoke", expectedId: null);
        await status("call-nesting.json", new
        {
            reachedDepth = limit,
            rejection = rejected.RootElement.GetProperty("error"),
            scopeRejection = scopedRejected.RootElement.GetProperty("error"),
            siblingDepth = Depth(sibling.RootElement),
            independentDepth = Depth(independent.RootElement),
            independentDeadline = independent.RootElement.GetProperty("context").GetProperty("deadlineUtc").GetString(),
            watchCount = watches.Count,
        });

        await Complete(independent.RootElement.GetProperty("id").GetString()!, "independent");
        await Complete(sibling.RootElement.GetProperty("id").GetString()!, "sibling");
        foreach (var call in calls.AsEnumerable().Reverse()) await Complete(call.HostId, call.WorkerId);
        foreach (var watch in watches)
        {
            await write(new { type = "worker.complete", id = watch.HostId });
            using var completed = await Next("host.complete", watch.WorkerId);
        }
        await write(new { type = "worker.result", id = rootId, value = new { accepted = true } });

        async Task Complete(string hostId, string workerId)
        {
            await write(new { type = "worker.result", id = hostId, value = new { accepted = true } });
            using var completed = await Next("host.result", workerId);
        }

        Task Send(string id, string? parentId, bool subscribe = false, string? scopeId = null)
        {
            var frame = new Dictionary<string, object?>
            {
                ["type"] = subscribe ? "worker.subscribe" : "worker.invoke",
                ["id"] = id,
                ["parentInvocationId"] = parentId,
                ["endpointReference"] = context.GetProperty("provider").GetProperty("endpointReference").GetString(),
                ["serviceId"] = "messages",
                ["methodId"] = subscribe ? "watch" : "send",
                ["request"] = new { message = id },
                ["deadlineUtc"] = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
            };
            if (scopeId is not null) frame.Add("scopeId", scopeId);
            return write(frame);
        }

        async Task<JsonDocument> Next(string expected, string? expectedId)
        {
            var frame = await read();
            var root = frame.RootElement;
            if (root.GetProperty("type").GetString() == expected
                && (expectedId is null || root.GetProperty("id").GetString() == expectedId)) return frame;
            var unexpected = root.GetRawText();
            frame.Dispose();
            throw new InvalidDataException($"Expected {expected}/{expectedId}, received {unexpected}");
        }
    }

    private static int Depth(JsonElement invocation)
        => invocation.GetProperty("context").GetProperty("callDepth").GetInt32();
}
