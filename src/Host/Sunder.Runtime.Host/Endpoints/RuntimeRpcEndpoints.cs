using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Endpoints;

internal static class RuntimeRpcEndpoints
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const long MaxRequestBodyBytes = 2L * 1024 * 1024;

    public static IEndpointRouteBuilder MapRuntimeRpcEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var rpc = endpoints.MapGroup("/rpc");
        rpc.MapGet("catalog", (RuntimeRpcCatalog catalog) =>
        {
            var snapshot = catalog.GetSnapshot();
            return Results.Ok(new RuntimeRpcCatalogSnapshot(
                snapshot.Revision,
                snapshot.Sequence,
                snapshot.Providers.Select(RuntimeRpcContractMapper.ToProtocol).ToArray(),
                snapshot.ResetRequired));
        });
        rpc.MapGet("catalog/events", (
            long? afterRevision,
            long? afterSequence,
            RuntimeRpcCatalog catalog) =>
        {
            if (afterRevision is < 0 || afterSequence is < 0)
            {
                throw new RuntimeValidationException("RPC catalog revisions and sequences cannot be negative.");
            }
            return Results.Ok(catalog.GetEventPage(afterRevision ?? 0, afterSequence ?? 0));
        });
        rpc.MapGet("permissions", (RuntimeRpcPermissionService permissions) =>
            Results.Ok(permissions.GetSnapshot()));
        rpc.MapPost("permissions/grant", (
            RuntimeRpcPermissionUpdateRequest request,
            RuntimeRpcPermissionService permissions,
            CancellationToken cancellationToken) =>
            permissions.SetAsync(request, RuntimeRpcPermissionState.Granted, cancellationToken))
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes));
        rpc.MapPost("permissions/revoke", (
            RuntimeRpcPermissionUpdateRequest request,
            RuntimeRpcPermissionService permissions,
            CancellationToken cancellationToken) =>
            permissions.SetAsync(request, RuntimeRpcPermissionState.Denied, cancellationToken))
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes));
        rpc.MapPost("app-sessions/open", OpenAppSessionAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes));
        rpc.MapPost("app-sessions/close", (
            RuntimeRpcAppSessionCloseRequest request,
            HttpContext context) =>
        {
            context.RequestServices.GetRequiredService<RuntimeRpcAppSessionManager>().Close(request.SessionId);
            return Results.NoContent();
        }).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes));
        rpc.MapPost("app/discover", DiscoverAppAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes));
        rpc.MapPost("app/provider", GetAppProviderAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes));
        rpc.MapPost("app/watch", WatchAppAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes));
        rpc.MapPost("app/invoke", InvokeAppAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes));
        rpc.MapPost("app/subscribe", SubscribeAppAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes));
        return endpoints;
    }

    private static Task<RuntimeRpcAppSessionDescriptor> OpenAppSessionAsync(
        RuntimeRpcAppSessionOpenRequest request,
        HttpContext context,
        CancellationToken cancellationToken)
        => context.RequestServices.GetRequiredService<RuntimeRpcAppSessionManager>()
            .OpenAsync(request, cancellationToken);

    private static async Task<IResult> DiscoverAppAsync(
        RuntimeRpcAppDiscoverRequest request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var broker = context.RequestServices.GetRequiredService<RuntimeRpcBroker>();
        try
        {
            var snapshot = await broker.DiscoverAppAsync(
                request.SessionId,
                request.ContractId,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(new RuntimeRpcAppDiscoverResponse(
                new RuntimeRpcCatalogSnapshot(
                    snapshot.Revision,
                    snapshot.Sequence,
                    snapshot.Providers.Select(RuntimeRpcContractMapper.ToProtocol).ToArray(),
                    snapshot.ResetRequired),
                null));
        }
        catch (SunderRpcException exception)
        {
            return Results.Ok(new RuntimeRpcAppDiscoverResponse(null, ToProtocol(exception.Error)));
        }
    }

    private static async Task<IResult> InvokeAppAsync(
        RuntimeRpcAppInvokeRequest request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var broker = context.RequestServices.GetRequiredService<RuntimeRpcBroker>();
        try
        {
            var response = await broker.InvokeAppAsync(
                request.SessionId,
                Endpoint(request.EndpointReference),
                request.ServiceId,
                request.MethodId,
                request.Request,
                new SunderRpcCallOptions(request.DeadlineUtc),
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(new RuntimeRpcAppInvokeResponse(response, null));
        }
        catch (SunderRpcException exception)
        {
            return Results.Ok(new RuntimeRpcAppInvokeResponse(null, ToProtocol(exception.Error)));
        }
    }

    private static async Task<IResult> GetAppProviderAsync(
        RuntimeRpcAppProviderRequest request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var broker = context.RequestServices.GetRequiredService<RuntimeRpcBroker>();
        try
        {
            var provider = await broker.GetProviderAppAsync(
                request.SessionId,
                Endpoint(request.EndpointReference),
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(new RuntimeRpcAppProviderResponse(
                provider is null ? null : RuntimeRpcContractMapper.ToProtocol(provider),
                null));
        }
        catch (SunderRpcException exception)
        {
            return Results.Ok(new RuntimeRpcAppProviderResponse(null, ToProtocol(exception.Error)));
        }
    }

    private static async Task WatchAppAsync(
        RuntimeRpcAppWatchRequest request,
        HttpContext context,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var broker = context.RequestServices.GetRequiredService<RuntimeRpcBroker>();
        response.ContentType = "application/x-ndjson; charset=utf-8";
        try
        {
            await foreach (var item in broker.WatchAppAsync(
                               request.SessionId,
                               request.AfterRevision,
                               request.AfterSequence,
                               cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await WriteFrameAsync(
                    response,
                    new RuntimeRpcAppCatalogFrame(
                        RuntimeRpcAppStreamFrameTypes.Event,
                        RuntimeRpcContractMapper.ToProtocol(item),
                        null),
                    cancellationToken).ConfigureAwait(false);
            }
            await WriteFrameAsync(
                response,
                new RuntimeRpcAppCatalogFrame(RuntimeRpcAppStreamFrameTypes.Completed, null, null),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await WriteFrameAsync(
                response,
                new RuntimeRpcAppCatalogFrame(
                    RuntimeRpcAppStreamFrameTypes.Error,
                    null,
                    ToProtocol(exception)),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SubscribeAppAsync(
        RuntimeRpcAppInvokeRequest request,
        HttpContext context,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var broker = context.RequestServices.GetRequiredService<RuntimeRpcBroker>();
        response.ContentType = "application/x-ndjson; charset=utf-8";
        try
        {
            await foreach (var item in broker.SubscribeAppAsync(
                               request.SessionId,
                               Endpoint(request.EndpointReference),
                               request.ServiceId,
                               request.MethodId,
                               request.Request,
                               new SunderRpcCallOptions(request.DeadlineUtc),
                               cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await WriteFrameAsync(
                    response,
                    new RuntimeRpcAppSubscriptionFrame(
                        RuntimeRpcAppStreamFrameTypes.Event,
                        item,
                        null),
                    cancellationToken).ConfigureAwait(false);
            }
            await WriteFrameAsync(
                response,
                new RuntimeRpcAppSubscriptionFrame(RuntimeRpcAppStreamFrameTypes.Completed, null, null),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await WriteFrameAsync(
                response,
                new RuntimeRpcAppSubscriptionFrame(
                    RuntimeRpcAppStreamFrameTypes.Error,
                    null,
                    ToProtocol(exception)),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static SunderRpcEndpointReference Endpoint(string value)
    {
        try
        {
            return new SunderRpcEndpointReference(value);
        }
        catch (ArgumentException)
        {
            throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.Validation,
                "rpc.endpoint.invalid",
                "The RPC endpoint reference is invalid."));
        }
    }

    private static RuntimeRpcErrorDescriptor ToProtocol(Exception exception)
        => exception is SunderRpcException rpcException
            ? ToProtocol(rpcException.Error)
            : exception is OperationCanceledException
                ? new RuntimeRpcErrorDescriptor(
                    RuntimeRpcErrorKind.Cancelled,
                    "rpc.call.cancelled",
                    "The RPC call was cancelled.")
                : new RuntimeRpcErrorDescriptor(
                    RuntimeRpcErrorKind.Unavailable,
                    "rpc.transport.unavailable",
                    "The Runtime RPC request could not be completed.");

    private static RuntimeRpcErrorDescriptor ToProtocol(SunderRpcError error)
        => new(
            error.Kind switch
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
            },
            error.Code,
            error.Message);

    private static async Task WriteFrameAsync<T>(
        HttpResponse response,
        T frame,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
        await response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await response.Body.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
