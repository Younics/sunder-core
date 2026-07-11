using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder endpoints, DateTimeOffset startedAtUtc)
    {
        endpoints.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        var group = endpoints.MapGroup("/system");
        group.MapGet(
            "",
            () => Results.Ok(new SystemStatusResponse("Sunder.Runtime.Host", RuntimeHostVersion.Current, true, startedAtUtc)));

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

        group.MapPost(
            "reset/prepare",
            (RuntimeResetChallengeService challenges) =>
            {
                var challenge = challenges.Create();
                return Results.Ok(new RuntimeResetChallengeResponse(challenge.Challenge, challenge.ExpiresAtUtc));
            });

        group.MapPost(
            "reset/drain",
            (RuntimeResetConfirmRequest request, RuntimeResetChallengeService challenges, IHostApplicationLifetime lifetime) =>
            {
                if (!challenges.TryConsume(request.Challenge))
                {
                    return Results.Conflict(new { error = "The Runtime reset confirmation challenge is missing, expired, or already used." });
                }

                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    lifetime.StopApplication();
                });

                return Results.Ok(new RuntimeResetDrainResponse([
                    "package-catalog-and-payloads",
                    "package-state-files-secrets-and-logs",
                    "uploads-and-snapshots",
                    "registry-credentials",
                    "runtime-connection",
                    "runtime-v1-root",
                ]));
            });

        return endpoints;
    }
}
