using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host;

internal sealed class RuntimeProblemDetailsMiddleware(
    RequestDelegate next,
    ILogger<RuntimeProblemDetailsMiddleware> logger)
{
    internal const string CorrelationHeader = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        context.TraceIdentifier = correlationId;
        context.Response.Headers[CorrelationHeader] = correlationId;

        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                await RuntimeProblemDetailsWriter.WriteAsync(context, new RuntimeCancellationException(), correlationId);
            }
        }
        catch (OperationCanceledException exception)
        {
            LogRpcFailureIfPresent(context, exception, correlationId);
            if (!context.Response.HasStarted)
            {
                await RuntimeProblemDetailsWriter.WriteAsync(context, new RuntimeCancellationException(), correlationId);
            }
        }
        catch (BadHttpRequestException exception)
        {
            if (!context.Response.HasStarted)
            {
                await RuntimeProblemDetailsWriter.WriteAsync(
                    context,
                    new RuntimeValidationException(exception.Message),
                    correlationId);
            }
        }
        catch (RuntimeException exception)
        {
            if (!context.Response.HasStarted)
            {
                await RuntimeProblemDetailsWriter.WriteAsync(context, exception, correlationId);
            }
        }
        catch (Exception exception)
        {
            LogUnhandledFailure(context, exception, correlationId);
            if (!context.Response.HasStarted)
            {
                await RuntimeProblemDetailsWriter.WriteInternalAsync(context, correlationId);
            }
        }
    }

    private void LogUnhandledFailure(HttpContext context, Exception exception, string correlationId)
    {
        var rpcException = FindRpcException(exception);
        if (rpcException is not null)
        {
            logger.LogError(
                "Unhandled Runtime API RPC failure. CorrelationId: {CorrelationId}; Method: {Method}; Route: {Route}; RpcKind: {RpcKind}; RpcCode: {RpcCode}",
                correlationId,
                context.Request.Method,
                context.Request.Path.Value,
                rpcException.Error.Kind,
                Bound(rpcException.Error.Code, 256));
            return;
        }

        logger.LogError(
            exception,
            "Unhandled Runtime API failure. CorrelationId: {CorrelationId}; Method: {Method}; Route: {Route}",
            correlationId,
            context.Request.Method,
            context.Request.Path.Value);
    }

    private void LogRpcFailureIfPresent(HttpContext context, Exception exception, string correlationId)
    {
        if (FindRpcException(exception) is not null)
        {
            LogUnhandledFailure(context, exception, correlationId);
        }
    }

    private static SunderRpcException? FindRpcException(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (current is SunderRpcException rpcException)
            {
                return rpcException;
            }
            if (current is AggregateException aggregate)
            {
                for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                {
                    pending.Push(aggregate.InnerExceptions[index]);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
        return null;
    }

    private static string Bound(string? value, int maximumLength)
        => string.IsNullOrWhiteSpace(value)
            ? "rpc.unknown"
            : value[..Math.Min(value.Length, maximumLength)];

    private static string ResolveCorrelationId(HttpContext context)
    {
        var supplied = context.Request.Headers[CorrelationHeader].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(supplied)
            && supplied.Length <= 128
            && supplied.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            return supplied;
        }

        return Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
    }
}

internal static class RuntimeProblemDetailsWriter
{
    public static Task WriteAsync(HttpContext context, RuntimeException exception, string correlationId)
    {
        if (exception is RuntimeAuthenticationException)
        {
            context.Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
        }

        return WriteAsync(context, Create(exception.Code, exception.Title, exception.StatusCode, exception.Message, correlationId));
    }

    public static Task WriteInternalAsync(HttpContext context, string correlationId)
        => WriteAsync(context, Create(
            RuntimeErrorCodes.Internal,
            "Internal Runtime error",
            StatusCodes.Status500InternalServerError,
            "The Runtime could not complete the request.",
            correlationId));

    internal static ProblemDetails Create(
        string code,
        string title,
        int statusCode,
        string detail,
        string correlationId)
    {
        var problem = new ProblemDetails
        {
            Type = $"https://sunder.dev/problems/{code}",
            Title = title,
            Status = statusCode,
            Detail = detail,
        };
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = correlationId;
        return problem;
    }

    private static async Task WriteAsync(HttpContext context, ProblemDetails problem)
    {
        var authenticate = context.Response.Headers[HeaderNames.WWWAuthenticate].ToString();
        context.Response.Clear();
        if (!string.IsNullOrWhiteSpace(authenticate))
        {
            context.Response.Headers[HeaderNames.WWWAuthenticate] = authenticate;
        }
        if (problem.Extensions.TryGetValue("correlationId", out var correlationId)
            && correlationId is not null)
        {
            context.Response.Headers[RuntimeProblemDetailsMiddleware.CorrelationHeader] = correlationId.ToString();
        }
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(problem), CancellationToken.None);
    }
}
