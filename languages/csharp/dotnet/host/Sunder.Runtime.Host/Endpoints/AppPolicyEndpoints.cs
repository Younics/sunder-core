using System.Text.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class AppPolicyEndpoints
{
    internal static IEndpointRouteBuilder MapAppPolicyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/app-policy-sessions");
        group.MapGet("/{sessionId}", (string sessionId, HttpContext context, AppProcessPolicySessions sessions) => RespondAsync(context,
            async () => Results.Ok(await sessions.GetStatusAsync(sessionId, AppPolicyPeerIdentity.Read(context), context.RequestAborted))));
        group.MapPost("", (HttpContext context, AppProcessPolicySessions sessions) => ReadAsync<RuntimeAppPolicySessionOpenRequest, RuntimeAppPolicySessionDescriptor>(context,
            request => sessions.OpenAsync(request, AppPolicyPeerIdentity.Read(context), context.RequestAborted)));
        group.MapPost("/{sessionId}/heartbeat", (string sessionId, HttpContext context, AppProcessPolicySessions sessions) => ReadAsync<RuntimeAppPolicySessionRequest, RuntimeAppPolicySessionStatus>(context,
            request => request.SessionId == sessionId ? sessions.HeartbeatAsync(request, AppPolicyPeerIdentity.Read(context), context.RequestAborted) : throw new PackagePolicyDeniedException(PackagePolicyErrorCodes.AppProcessSupervisionRequired)));
        group.MapPost("/{sessionId}/load-permit", (string sessionId, HttpContext context, AppProcessPolicySessions sessions) => ReadAsync<RuntimeAppPackageLoadPermitRequest, RuntimeAppPackageLoadPermitResult>(context,
            request => request.Session.SessionId == sessionId ? sessions.PermitAsync(request, AppPolicyPeerIdentity.Read(context), context.RequestAborted) : throw new PackagePolicyDeniedException(PackagePolicyErrorCodes.AppProcessSupervisionRequired)));
        group.MapPost("/{sessionId}/shutdown", (string sessionId, HttpContext context, AppProcessPolicySessions sessions) => ReadAsync<RuntimeAppPolicySessionRequest, RuntimeAppPolicySessionStatus>(context,
            request => request.SessionId == sessionId ? sessions.ShutdownAsync(request, AppPolicyPeerIdentity.Read(context), context.RequestAborted) : throw new PackagePolicyDeniedException(PackagePolicyErrorCodes.AppProcessSupervisionRequired)));
        return endpoints;
    }

    private static async Task<IResult> ReadAsync<T, TResult>(HttpContext context, Func<T, Task<TResult>> operation)
        => await RespondAsync(context, async () =>
        {
            if (context.Request.ContentLength > 64 * 1024 || !context.Request.HasJsonContentType()) return Results.BadRequest();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            using var bytes = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                var count = await context.Request.Body.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, 64 * 1024 + 1 - bytes.Length)), deadline.Token);
                if (count == 0) break;
                bytes.Write(chunk, 0, count);
                if (bytes.Length > 64 * 1024) return Results.StatusCode(413);
            }
            var request = JsonSerializer.Deserialize<T>(bytes.ToArray(), RegistryActivePolicyClient.Json) ?? throw new JsonException();
            return Results.Ok(await operation(request));
        });

    private static async Task<IResult> RespondAsync(HttpContext context, Func<Task<IResult>> operation)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return await operation();
        }
        catch (PackagePolicyDeniedException error)
        { return Results.Json(RuntimeProblemDetailsWriter.Create(error.Code, "App policy supervision required", 409, error.Code, context.TraceIdentifier), statusCode: 409, contentType: "application/problem+json"); }
        catch (JsonException) { return Results.BadRequest(); }
        catch (InvalidDataException)
        { return Results.Json(RuntimeProblemDetailsWriter.Create(PackagePolicyErrorCodes.AppProcessSupervisionRequired, "App process identity is invalid", 409, "App process identity could not be verified.", context.TraceIdentifier), statusCode: 409, contentType: "application/problem+json"); }
    }
}
