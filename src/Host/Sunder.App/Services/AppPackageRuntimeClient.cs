using System.Text.Json;
using System.Runtime.CompilerServices;
using Sunder.Runtime.Client;
using Sunder.Sdk.Runtime;

namespace Sunder.App.Services;

internal sealed class AppPackageRuntimeClient(
    string packageId,
    RuntimePackageOperationClient client,
    AppPackageGenerationPublication? publication = null) : IPackageRuntimeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string UnavailableCode = "runtime.v1.unavailable";
    private const string TimeoutCode = "runtime.v1.timeout";
    private const string TransportCode = "runtime.v1.transport-error";
    private const string ProtocolCode = "runtime.v1.protocol-incompatible";

    public bool IsAvailable => publication?.IsRuntimeAvailable ?? true;

    public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        PackageRuntimeOperation<TRequest, TResponse> operation,
        TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : class
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        try
        {
            var revocationToken = publication?.RequireRuntimeAccess("Runtime operations") ?? default;
            using var generationCancellation = CreateGenerationCancellation(
                cancellationToken,
                revocationToken);
            var response = await client.InvokeAsync(
                packageId,
                operation.OperationId,
                payload,
                generationCancellation?.Token ?? cancellationToken).ConfigureAwait(false);
            revocationToken.ThrowIfCancellationRequested();
            return JsonSerializer.Deserialize<TResponse>(response, JsonOptions)
                   ?? throw new InvalidDataException(
                       $"Package Runtime operation '{operation.OperationId}' returned an empty response.");
        }
        catch (Exception exception) when (TryTranslateFailure(
                   exception,
                   cancellationToken,
                   $"operation '{operation.OperationId}'",
                   out var translated))
        {
            throw translated;
        }
    }

    public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
        PackageRuntimeStream<TRequest, TEvent> stream,
        TRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TRequest : class
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        IAsyncEnumerator<byte[]>? enumerator = null;
        CancellationTokenSource? generationCancellation = null;
        try
        {
            var revocationToken = publication?.RequireRuntimeAccess("Runtime streams") ?? default;
            generationCancellation = CreateGenerationCancellation(cancellationToken, revocationToken);
            var operationCancellation = generationCancellation?.Token ?? cancellationToken;
            enumerator = client.SubscribeAsync(
                    packageId,
                    stream.StreamId,
                    payload,
                    operationCancellation)
                .GetAsyncEnumerator(operationCancellation);
            while (await MoveNextAsync(
                       enumerator,
                       cancellationToken,
                       $"stream '{stream.StreamId}'").ConfigureAwait(false))
            {
                operationCancellation.ThrowIfCancellationRequested();
                yield return JsonSerializer.Deserialize<TEvent>(enumerator.Current, JsonOptions)
                             ?? throw new InvalidDataException(
                                 $"Package Runtime stream '{stream.StreamId}' returned an empty event.");
            }
        }
        finally
        {
            try
            {
                if (enumerator is not null)
                {
                    await DisposeAsync(
                        enumerator,
                        cancellationToken,
                        $"stream '{stream.StreamId}' disposal").ConfigureAwait(false);
                }
            }
            finally
            {
                generationCancellation?.Dispose();
            }
        }
    }

    private static CancellationTokenSource? CreateGenerationCancellation(
        CancellationToken callerCancellationToken,
        CancellationToken revocationToken)
        => revocationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken, revocationToken)
            : null;

    private async ValueTask DisposeAsync(
        IAsyncEnumerator<byte[]> enumerator,
        CancellationToken cancellationToken,
        string operation)
    {
        try
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (TryTranslateFailure(
                   exception,
                   cancellationToken,
                   operation,
                   out var translated))
        {
            throw translated;
        }
    }

    private async ValueTask<bool> MoveNextAsync(
        IAsyncEnumerator<byte[]> enumerator,
        CancellationToken cancellationToken,
        string operation)
    {
        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (TryTranslateFailure(
                   exception,
                   cancellationToken,
                   operation,
                   out var translated))
        {
            throw translated;
        }
    }

    private bool TryTranslateFailure(
        Exception exception,
        CancellationToken callerCancellationToken,
        string operation,
        out PackageRuntimeInvocationException translated)
    {
        if (exception is PackageRuntimeInvocationException
            || exception is OperationCanceledException && callerCancellationToken.IsCancellationRequested)
        {
            translated = null!;
            return false;
        }

        translated = exception switch
        {
            RuntimeClientException runtime => TranslateRuntimeProblem(runtime),
            OperationCanceledException when publication?.IsRevoked == true => new PackageRuntimeInvocationException(
                UnavailableCode,
                isTransient: true,
                statusCode: 503),
            OperationCanceledException => new PackageRuntimeInvocationException(
                TimeoutCode,
                isTransient: true),
            TimeoutException => new PackageRuntimeInvocationException(
                TimeoutCode,
                isTransient: true),
            HttpRequestException transport => new PackageRuntimeInvocationException(
                TransportCode,
                isTransient: true,
                statusCode: transport.StatusCode is null ? null : (int)transport.StatusCode.Value),
            RuntimePackageStreamException stream => new PackageRuntimeInvocationException(
                IsSafeLowercaseToken(stream.Code) ? stream.Code : "runtime.v1.stream-failed",
                isTransient: false),
            IOException => new PackageRuntimeInvocationException(
                TransportCode,
                isTransient: true),
            RuntimeProtocolException => new PackageRuntimeInvocationException(
                ProtocolCode,
                isTransient: false),
            ObjectDisposedException => new PackageRuntimeInvocationException(
                UnavailableCode,
                isTransient: true,
                statusCode: 503),
            InvalidOperationException => new PackageRuntimeInvocationException(
                UnavailableCode,
                isTransient: true,
                statusCode: 503),
            UriFormatException => new PackageRuntimeInvocationException(
                UnavailableCode,
                isTransient: true,
                statusCode: 503),
            _ => null!,
        };
        if (translated is null)
        {
            return false;
        }

        AppSessionLog.WriteError(
            $"Package Runtime {operation} failed for '{packageId}' with package-visible code '{translated.Code}'"
            + (translated.CorrelationId is null ? "." : $" and correlation '{translated.CorrelationId}'."),
            exception,
            visibleInDeveloperLog: false);
        return true;
    }

    private static PackageRuntimeInvocationException TranslateRuntimeProblem(RuntimeClientException exception)
    {
        var statusCode = (int)exception.StatusCode;
        var code = IsSafeLowercaseToken(exception.ErrorCode)
            ? exception.ErrorCode!
            : "runtime.v1.request-failed";
        var correlationId = IsSafeToken(exception.CorrelationId)
            ? exception.CorrelationId
            : null;
        var isTransient = statusCode is 408 or 425 or 429 or 499 or >= 500 and <= 599;
        return new PackageRuntimeInvocationException(
            code,
            isTransient,
            statusCode,
            correlationId);
    }

    private static bool IsSafeLowercaseToken(string? value)
        => IsSafeToken(value)
           && value!.All(static character => !char.IsAsciiLetter(character)
                                            || char.IsAsciiLetterLower(character));

    private static bool IsSafeToken(string? value)
        => value is { Length: > 0 and <= 128 }
           && value.All(static character => char.IsAsciiLetterOrDigit(character)
                                           || character is '.' or '-' or '_');
}
