using System.Text.Json;
using System.Runtime.CompilerServices;
using Sunder.Runtime.Client;
using Sunder.Sdk.Runtime;

namespace Sunder.App.Services;

internal sealed class AppPackageRuntimeClient(
    string packageId,
    RuntimePackageOperationClient client) : IPackageRuntimeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsAvailable => true;

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
        var response = await client.InvokeAsync(
            packageId,
            operation.OperationId,
            payload,
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TResponse>(response, JsonOptions)
               ?? throw new InvalidDataException(
                   $"Package Runtime operation '{operation.OperationId}' returned an empty response.");
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
        await foreach (var value in client.SubscribeAsync(
                           packageId,
                           stream.StreamId,
                           payload,
                           cancellationToken))
        {
            yield return JsonSerializer.Deserialize<TEvent>(value, JsonOptions)
                         ?? throw new InvalidDataException(
                             $"Package Runtime stream '{stream.StreamId}' returned an empty event.");
        }
    }
}
