using Microsoft.AspNetCore.Mvc;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Endpoints;

internal static class RuntimeWorkerExecutionObservationEndpoints
{
    internal static void MapWorkerExecutionObservationEndpoints(this RouteGroupBuilder rpc)
    {
        rpc.MapPost("app/runtime-observations/capture-execution", CaptureAsync)
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024));
        rpc.MapPost("app/runtime-observations/observe-execution", ObserveAsync)
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024));
    }

    private static async Task<IResult> CaptureAsync(RuntimeRpcAppExecutionCaptureRequest request,
        HttpContext context, CancellationToken token)
    {
        try
        {
            var reference = await context.RequestServices.GetRequiredService<PackageWorkerExecutionObservationService>()
                .CaptureAsync(request, token).ConfigureAwait(false);
            return Results.Ok(new RuntimeRpcAppExecutionCaptureResponse(reference, null));
        }
        catch (SunderRpcException error)
        { return Results.Ok(new RuntimeRpcAppExecutionCaptureResponse(null, ToProtocol(error.Error))); }
        catch (OperationCanceledException)
        { return Results.Ok(new RuntimeRpcAppExecutionCaptureResponse(null, Cancelled())); }
    }

    private static async Task<IResult> ObserveAsync(RuntimeRpcAppExecutionObserveRequest request,
        HttpContext context, CancellationToken token)
    {
        try
        {
            var observation = await context.RequestServices.GetRequiredService<PackageWorkerExecutionObservationService>()
                .ObserveAsync(request, token).ConfigureAwait(false);
            return Results.Ok(new RuntimeRpcAppExecutionObserveResponse(observation, null));
        }
        catch (SunderRpcException error)
        { return Results.Ok(new RuntimeRpcAppExecutionObserveResponse(null, ToProtocol(error.Error))); }
        catch (OperationCanceledException)
        { return Results.Ok(new RuntimeRpcAppExecutionObserveResponse(null, Cancelled())); }
    }

    private static RuntimeRpcErrorDescriptor Cancelled()
        => new(RuntimeRpcErrorKind.Cancelled, "rpc.execution.cancelled", "The execution observation was cancelled.");

    private static RuntimeRpcErrorDescriptor ToProtocol(SunderRpcError error)
        => new(error.Kind switch
        {
            SunderRpcErrorKind.Domain => RuntimeRpcErrorKind.Domain,
            SunderRpcErrorKind.PermissionDenied => RuntimeRpcErrorKind.PermissionDenied,
            SunderRpcErrorKind.NotFound => RuntimeRpcErrorKind.NotFound,
            SunderRpcErrorKind.StaleEndpoint => RuntimeRpcErrorKind.StaleEndpoint,
            SunderRpcErrorKind.Validation => RuntimeRpcErrorKind.Validation,
            SunderRpcErrorKind.DeadlineExceeded => RuntimeRpcErrorKind.DeadlineExceeded,
            SunderRpcErrorKind.Cancelled => RuntimeRpcErrorKind.Cancelled,
            SunderRpcErrorKind.ResourceExhausted => RuntimeRpcErrorKind.ResourceExhausted,
            SunderRpcErrorKind.Unavailable => RuntimeRpcErrorKind.Unavailable,
            SunderRpcErrorKind.ProviderFaulted => RuntimeRpcErrorKind.ProviderFaulted,
            _ => RuntimeRpcErrorKind.Protocol,
        }, error.Code, error.Message,
            error.DispatchState == SunderRpcDispatchState.NotDispatched ? RuntimeRpcDispatchState.NotDispatched : RuntimeRpcDispatchState.PossiblyDispatched,
            error.RetrySafety == SunderRpcRetrySafety.Safe ? RuntimeRpcRetrySafety.Safe : RuntimeRpcRetrySafety.Unsafe,
            error.Details);
}
