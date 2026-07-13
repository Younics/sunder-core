using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Sdk.Runtime;

namespace Sunder.Runtime.Host.Services;

internal abstract class RuntimePackageStreamRegistration(string streamId)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string StreamId { get; } = streamId;

    public abstract IAsyncEnumerable<byte[]> SubscribeAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);

    protected static TRequest Deserialize<TRequest>(ReadOnlyMemory<byte> payload) where TRequest : class
    {
        try
        {
            return JsonSerializer.Deserialize<TRequest>(payload.Span, JsonOptions)
                   ?? throw new RuntimeValidationException("The package Runtime stream request body is invalid.");
        }
        catch (JsonException)
        {
            throw new RuntimeValidationException("The package Runtime stream request body is invalid.");
        }
    }

    protected static byte[] Serialize<TEvent>(TEvent value) where TEvent : class
        => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
}

internal sealed class RuntimePackageStreamRegistration<TRequest, TEvent>(
    PackageRuntimeStream<TRequest, TEvent> stream,
    IPackageRuntimeStreamHandler<TRequest, TEvent> handler)
    : RuntimePackageStreamRegistration(stream.StreamId)
    where TRequest : class
    where TEvent : class
{
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(
        ReadOnlyMemory<byte> payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = Deserialize<TRequest>(payload);
        await foreach (var value in handler.SubscribeAsync(request, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            if (value is null)
            {
                throw new InvalidOperationException($"Package Runtime stream '{StreamId}' returned a null event.");
            }
            yield return Serialize(value);
        }
    }
}
