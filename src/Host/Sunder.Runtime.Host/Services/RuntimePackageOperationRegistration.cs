using System.Text.Json;
using Sunder.Sdk.Runtime;

namespace Sunder.Runtime.Host.Services;

internal abstract class RuntimePackageOperationRegistration(string operationId)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string OperationId { get; } = operationId;

    public abstract ValueTask<byte[]> InvokeAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    protected static TRequest Deserialize<TRequest>(ReadOnlyMemory<byte> payload) where TRequest : class
        => JsonSerializer.Deserialize<TRequest>(payload.Span, JsonOptions)
           ?? throw new RuntimeValidationException("The package Runtime operation request body is invalid.");

    protected static byte[] Serialize<TResponse>(TResponse response) where TResponse : class
        => JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
}

internal sealed class RuntimePackageOperationRegistration<TRequest, TResponse>(
    PackageRuntimeOperation<TRequest, TResponse> operation,
    IPackageRuntimeOperationHandler<TRequest, TResponse> handler)
    : RuntimePackageOperationRegistration(operation.OperationId)
    where TRequest : class
    where TResponse : class
{
    public override async ValueTask<byte[]> InvokeAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        TRequest request;
        try
        {
            request = Deserialize<TRequest>(payload);
        }
        catch (JsonException)
        {
            throw new RuntimeValidationException("The package Runtime operation request body is invalid.");
        }

        var response = await handler.HandleAsync(request, cancellationToken)
            ?? throw new InvalidOperationException($"Package Runtime operation '{OperationId}' returned null.");
        return Serialize(response);
    }
}
