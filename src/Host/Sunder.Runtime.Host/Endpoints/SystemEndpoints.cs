using Sunder.Protocol;

namespace Sunder.Runtime.Host.Endpoints;

internal static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder endpoints, DateTimeOffset startedAtUtc)
    {
        endpoints.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        var group = endpoints.MapGroup("/api/system");
        group.MapGet(
            "",
            () => Results.Ok(new SystemStatusResponse("Sunder.Runtime.Host", "0.1.0", true, startedAtUtc)));

        group.MapPost(
            "shutdown",
            (IHostApplicationLifetime lifetime) =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    lifetime.StopApplication();
                });

                return Results.Ok();
            });

        return endpoints;
    }
}
