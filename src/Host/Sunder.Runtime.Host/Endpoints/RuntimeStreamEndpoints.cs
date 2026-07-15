using System.Text.Json;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class RuntimeStreamEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapRuntimeStreamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var runtime = endpoints.MapGroup("/runtime-events");
        runtime.MapGet("snapshot", (long? after, RuntimeEventStreamService events) =>
            Results.Ok(events.GetSnapshot(Math.Max(0, after ?? 0))));
        runtime.MapGet("stream", StreamRuntimeEventsAsync);

        var logs = endpoints.MapGroup("/package-logs");
        logs.MapGet("snapshot", (long? after, int? limit, PackageLogStreamService packageLogs) =>
            Results.Ok(packageLogs.GetSnapshot(Math.Max(0, after ?? 0), limit ?? 500)));
        logs.MapGet("stream", StreamPackageLogsAsync);

        return endpoints;
    }

    private static async Task StreamRuntimeEventsAsync(
        HttpContext context,
        RuntimeEventStreamService events,
        long? after,
        CancellationToken cancellationToken)
    {
        var sequenceId = ResolveSequenceId(context, after);
        await using var subscription = events.Subscribe(sequenceId);
        await WriteStreamAsync(context, subscription.Reader, "runtime", cancellationToken);
    }

    private static async Task StreamPackageLogsAsync(
        HttpContext context,
        PackageLogStreamService packageLogs,
        long? after,
        CancellationToken cancellationToken)
    {
        var sequenceId = ResolveSequenceId(context, after);
        await using var subscription = packageLogs.Subscribe(sequenceId);
        if (subscription.HistoryGap)
        {
            throw new RuntimeConflictException("The requested package log history is no longer retained.");
        }

        await WriteStreamAsync(context, subscription.Reader, "package-log", cancellationToken);
    }

    private static async Task WriteStreamAsync<T>(
        HttpContext context,
        System.Threading.Channels.ChannelReader<T> reader,
        string eventName,
        CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        try
        {
            await foreach (var item in reader.ReadAllAsync(cancellationToken))
            {
                var sequenceId = item switch
                {
                    RuntimeEventDescriptor runtimeEvent => runtimeEvent.SequenceId,
                    PackageLogEntryDescriptor packageLog => packageLog.SequenceId,
                    _ => 0,
                };
                await context.Response.WriteAsync($"id: {sequenceId}\nevent: {eventName}\ndata: {JsonSerializer.Serialize(item, JsonOptions)}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SlowRuntimeStreamConsumerException)
        {
            context.Abort();
        }
    }

    private static long ResolveSequenceId(HttpContext context, long? after)
    {
        if (after is >= 0)
        {
            return after.Value;
        }

        return long.TryParse(context.Request.Headers["Last-Event-ID"].ToString(), out var headerValue)
            ? Math.Max(0, headerValue)
            : 0;
    }
}
