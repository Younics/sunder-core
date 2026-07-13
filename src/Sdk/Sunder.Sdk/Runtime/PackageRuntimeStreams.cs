using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Runtime;

/// <summary>Identifies one typed event stream implemented by a package's Runtime module.</summary>
/// <typeparam name="TRequest">Package-owned subscription request type.</typeparam>
/// <typeparam name="TEvent">Package-owned event type.</typeparam>
[SunderSdkCapability(SunderSdkCapabilities.RuntimeOperationsV1)]
public sealed class PackageRuntimeStream<TRequest, TEvent>
    where TRequest : class
    where TEvent : class
{
    /// <summary>Creates a stream with a stable lowercase package-scoped identifier.</summary>
    /// <exception cref="ArgumentException">The identifier is empty, too long, or contains unsupported characters.</exception>
    public PackageRuntimeStream(string streamId)
    {
        if (!PackageRuntimeContractId.IsValid(streamId))
        {
            throw new ArgumentException(
                "Package Runtime stream ids must be lowercase ASCII tokens of at most 128 characters.",
                nameof(streamId));
        }

        StreamId = streamId;
    }

    /// <summary>Gets the stable package-scoped stream identifier.</summary>
    public string StreamId { get; }
}

/// <summary>Produces one typed package Runtime event stream.</summary>
/// <remarks>Each subscription is independent. The handler must observe cancellation and must not return null events.</remarks>
/// <typeparam name="TRequest">Package-owned subscription request type.</typeparam>
/// <typeparam name="TEvent">Package-owned event type.</typeparam>
[SunderSdkCapability(SunderSdkCapabilities.RuntimeOperationsV1)]
public interface IPackageRuntimeStreamHandler<in TRequest, TEvent>
    where TRequest : class
    where TEvent : class
{
    /// <summary>Subscribes to events until completion or cancellation.</summary>
    IAsyncEnumerable<TEvent> SubscribeAsync(
        TRequest request,
        CancellationToken cancellationToken = default);
}
