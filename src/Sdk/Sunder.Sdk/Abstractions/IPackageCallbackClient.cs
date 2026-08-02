using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Starts and observes host-routed callback sessions for the current package.</summary>
/// <remarks>This capability is available to App activations only. Runtime and preflight contexts expose an unavailable client.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public interface IPackageCallbackClient
{
    /// <summary>Gets whether callback sessions are available in the current host context.</summary>
    bool IsAvailable { get; }

    /// <summary>Starts or reuses a pending session for a stable package-local handler id.</summary>
    ValueTask<PackageCallbackSessionStatus> StartAsync(
        string callbackHandlerId,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the current status of a session created by this package activation.</summary>
    ValueTask<PackageCallbackSessionStatus> GetStatusAsync(
        string callbackSessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Requests cancellation of a pending session, returning false when it is absent or no longer pending.</summary>
    ValueTask<bool> CancelAsync(
        string callbackSessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Polls until a session is terminal or throws <see cref="TimeoutException"/> after the required timeout.</summary>
    ValueTask<PackageCallbackSessionStatus> WaitForCompletionAsync(
        string callbackSessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>Opens an absolute launch URI through the App host shell.</summary>
    ValueTask OpenLaunchUriAsync(Uri launchUri, CancellationToken cancellationToken = default);
}

/// <summary>Represents host contexts that cannot launch App callback sessions.</summary>
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public sealed class NullPackageCallbackClient : IPackageCallbackClient
{
    /// <summary>Gets the shared stateless instance.</summary>
    public static NullPackageCallbackClient Instance { get; } = new();

    private NullPackageCallbackClient() { }

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public ValueTask<PackageCallbackSessionStatus> StartAsync(
        string callbackHandlerId,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackHandlerId);
        cancellationToken.ThrowIfCancellationRequested();
        _ = PackageCallbackParameters.CopyAndValidate(parameters);
        return Unavailable(callbackHandlerId, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PackageCallbackSessionStatus> GetStatusAsync(
        string callbackSessionId,
        CancellationToken cancellationToken = default)
        => Unavailable(callbackSessionId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> CancelAsync(
        string callbackSessionId,
        CancellationToken cancellationToken = default)
        => Unavailable<bool>(callbackSessionId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<PackageCallbackSessionStatus> WaitForCompletionAsync(
        string callbackSessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackSessionId);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The callback wait timeout must be positive.");
        }

        return Unavailable<PackageCallbackSessionStatus>(callbackSessionId, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask OpenLaunchUriAsync(Uri launchUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchUri);
        if (!launchUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The launch URI must be absolute.", nameof(launchUri));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException(new NotSupportedException("Package callback launching is available only in an App activation."));
    }

    private static ValueTask<PackageCallbackSessionStatus> Unavailable(string value, CancellationToken cancellationToken)
        => Unavailable<PackageCallbackSessionStatus>(value, cancellationToken);

    private static ValueTask<T> Unavailable<T>(string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException<T>(
            new NotSupportedException("Package callback sessions are available only in an App activation."));
    }
}
