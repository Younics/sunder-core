using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
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
            (RuntimeSnapshotService snapshots) =>
            {
                var state = snapshots.GetSnapshot().BootstrapState;
                return Results.Ok(new SystemStatusResponse(
                    "Sunder.Runtime.Host",
                    RuntimeHostVersion.Current,
                    state == RuntimeBootstrapState.Ready,
                    startedAtUtc)
                {
                    State = state,
                });
            });

        group.MapPost(
            "shutdown",
            (HttpResponse response, IHostApplicationLifetime lifetime) =>
            {
                response.OnCompleted(() =>
                {
                    lifetime.StopApplication();
                    return Task.CompletedTask;
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
            (RuntimeResetConfirmRequest request, RuntimeResetChallengeService challenges, HttpResponse response, IHostApplicationLifetime lifetime) =>
            {
                if (!challenges.TryConsume(request.Challenge))
                {
                    throw new RuntimeConflictException(
                        "The Runtime reset confirmation challenge is missing, expired, or already used.");
                }

                response.OnCompleted(() =>
                {
                    lifetime.StopApplication();
                    return Task.CompletedTask;
                });

                return Results.Ok(new RuntimeResetDrainResponse(
                    RuntimeV1StateDescriptor.ResetCategories.Select(static category => category.Id).ToArray()));
            });

        return endpoints;
    }
}
