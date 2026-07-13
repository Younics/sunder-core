using System.Buffers;
using System.Text.Json;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class PackageRuntimeOperationEndpoints
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    public static IEndpointRouteBuilder MapPackageRuntimeOperationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/packages/{packageId}/operations/{operationId}", InvokeAsync);
        endpoints.MapPost("/packages/{packageId}/streams/{streamId}", SubscribeAsync);
        return endpoints;
    }

    private static async Task<IResult> InvokeAsync(
        string packageId,
        string operationId,
        HttpRequest request,
        RuntimePackageOperationService operations,
        CancellationToken cancellationToken)
    {
        if (!PackageDataInputValidator.IsPackageId(packageId)
            || !PackageDataInputValidator.IsKey(operationId))
        {
            throw new RuntimeValidationException("The package id or Runtime operation id is invalid.");
        }
        var payload = await ReadPayloadAsync(request, operations.MaxRequestBytes, cancellationToken);

        var response = await operations.InvokeAsync(
            packageId,
            operationId,
            payload,
            cancellationToken);
        return Results.Bytes(response, "application/json; charset=utf-8");
    }

    private static async Task SubscribeAsync(
        string packageId,
        string streamId,
        HttpRequest request,
        HttpResponse response,
        RuntimePackageOperationService operations,
        CancellationToken cancellationToken)
    {
        if (!PackageDataInputValidator.IsPackageId(packageId)
            || !PackageDataInputValidator.IsKey(streamId))
        {
            throw new RuntimeValidationException("The package id or Runtime stream id is invalid.");
        }

        var payload = await ReadPayloadAsync(request, operations.MaxRequestBytes, cancellationToken);
        var values = operations.SubscribeAsync(packageId, streamId, payload, cancellationToken);
        await using var enumerator = values.GetAsyncEnumerator(cancellationToken);
        response.ContentType = "application/x-ndjson; charset=utf-8";
        var wroteEvent = false;
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                var frame = CreateEventFrame(enumerator.Current);
                if (frame.Length > operations.MaxStreamRecordBytes)
                {
                    throw new RuntimeUploadLimitException(
                        $"Package Runtime stream record exceeds the {operations.MaxStreamRecordBytes} byte limit.");
                }
                await WriteFrameAsync(response, frame, cancellationToken);
                wroteEvent = true;
            }

            await WriteFrameAsync(response, CreateTerminalFrame(PackageRuntimeStreamFrameTypes.Completed), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (wroteEvent || response.HasStarted)
        {
            var message = string.IsNullOrWhiteSpace(exception.Message)
                ? "Package Runtime stream handler failed."
                : exception.Message;
            if (message.Length > operations.MaxStreamErrorMessageCharacters)
            {
                message = message[..operations.MaxStreamErrorMessageCharacters];
            }
            await WriteFrameAsync(
                response,
                CreateErrorFrame("runtime.package-stream.handler-error", message),
                cancellationToken);
        }
    }

    private static async Task<byte[]> ReadPayloadAsync(
        HttpRequest request,
        int maxRequestBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > 0 && request.ContentLength > maxRequestBytes)
        {
            throw new RuntimeUploadLimitException(
                $"Package Runtime request exceeds the {maxRequestBytes} byte limit.");
        }

        using var payload = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int bytesRead;
        while ((bytesRead = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (payload.Length + bytesRead > maxRequestBytes)
            {
                throw new RuntimeUploadLimitException(
                    $"Package Runtime request exceeds the {maxRequestBytes} byte limit.");
            }
            await payload.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
        }
        return payload.ToArray();
    }

    private static byte[] CreateEventFrame(ReadOnlySpan<byte> value)
    {
        var output = new ArrayBufferWriter<byte>(value.Length + 64);
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString("type", PackageRuntimeStreamFrameTypes.Event);
        writer.WritePropertyName("event");
        writer.WriteRawValue(value, skipInputValidation: false);
        writer.WriteEndObject();
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    private static byte[] CreateTerminalFrame(string type)
        => JsonSerializer.SerializeToUtf8Bytes(new { type }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static byte[] CreateErrorFrame(string code, string message)
        => JsonSerializer.SerializeToUtf8Bytes(
            new { type = PackageRuntimeStreamFrameTypes.Error, error = new PackageRuntimeStreamError(code, message) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static async Task WriteFrameAsync(
        HttpResponse response,
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken)
    {
        await response.Body.WriteAsync(frame, cancellationToken);
        await response.Body.WriteAsync(NewLine, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
