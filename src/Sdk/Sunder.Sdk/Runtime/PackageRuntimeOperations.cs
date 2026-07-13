using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Runtime;

/// <summary>Identifies one typed request/response operation implemented by a package's Runtime module.</summary>
/// <typeparam name="TRequest">Package-owned request type serialized across the App-to-Runtime boundary.</typeparam>
/// <typeparam name="TResponse">Package-owned response type serialized across the App-to-Runtime boundary.</typeparam>
[SunderSdkCapability(SunderSdkCapabilities.RuntimeOperationsV1)]
public sealed class PackageRuntimeOperation<TRequest, TResponse>
    where TRequest : class
    where TResponse : class
{
    /// <summary>Creates an operation with a stable lowercase package-scoped identifier.</summary>
    /// <exception cref="ArgumentException">The identifier is empty, too long, or contains unsupported characters.</exception>
    public PackageRuntimeOperation(string operationId)
    {
        if (!PackageRuntimeContractId.IsValid(operationId))
        {
            throw new ArgumentException(
                "Package Runtime operation ids must be lowercase ASCII tokens of at most 128 characters.",
                nameof(operationId));
        }

        OperationId = operationId;
    }

    /// <summary>Gets the stable package-scoped operation identifier.</summary>
    public string OperationId { get; }

}

internal static class PackageRuntimeContractId
{
    internal static bool IsValid(string value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= 128
           && value.All(character => char.IsAsciiLetterOrDigit(character)
               || character is '.' or '-' or '_')
           && value.All(character => !char.IsAsciiLetter(character) || char.IsAsciiLetterLower(character));
}

/// <summary>Handles one typed package Runtime operation inside the package's Runtime activation.</summary>
/// <remarks>Handlers may be called concurrently. Cancellation is signaled when the caller disconnects or Runtime shuts down.</remarks>
/// <typeparam name="TRequest">Package-owned request type.</typeparam>
/// <typeparam name="TResponse">Package-owned response type.</typeparam>
[SunderSdkCapability(SunderSdkCapabilities.RuntimeOperationsV1)]
public interface IPackageRuntimeOperationHandler<in TRequest, TResponse>
    where TRequest : class
    where TResponse : class
{
    /// <summary>Handles the request and returns a non-null response.</summary>
    ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Invokes operations registered by the current package's Runtime activation.</summary>
/// <remarks>The client is package-scoped. It cannot invoke another package's handlers.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.RuntimeOperationsV1)]
public interface IPackageRuntimeClient
{
    /// <summary>Gets whether an authenticated Runtime operation channel is available.</summary>
    bool IsAvailable { get; }

    /// <summary>Invokes a typed operation in the current package's Runtime activation.</summary>
    ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        PackageRuntimeOperation<TRequest, TResponse> operation,
        TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : class
        where TResponse : class;

    /// <summary>Subscribes to a typed event stream from the current package's Runtime activation.</summary>
    IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
        PackageRuntimeStream<TRequest, TEvent> stream,
        TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : class
        where TEvent : class;
}

/// <summary>Represents host contexts that do not expose a package Runtime operation channel.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RuntimeOperationsV1)]
public sealed class NullPackageRuntimeClient : IPackageRuntimeClient
{
    /// <summary>Gets the shared stateless instance.</summary>
    public static NullPackageRuntimeClient Instance { get; } = new();

    private NullPackageRuntimeClient()
    {
    }

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        PackageRuntimeOperation<TRequest, TResponse> operation,
        TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : class
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException<TResponse>(
            new NotSupportedException("Package Runtime operations are not available in this host context."));
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
        PackageRuntimeStream<TRequest, TEvent> stream,
        TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : class
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(request);
        return Unavailable<TEvent>(cancellationToken);
    }

    private static async IAsyncEnumerable<TEvent> Unavailable<TEvent>(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        throw new NotSupportedException("Package Runtime streams are not available in this host context.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
