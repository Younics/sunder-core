using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Registers accepted App state work before debounce, asynchronous validation, or storage begins.</summary>
/// <remarks>
/// Available only to published App activations. Admission failures are thrown synchronously, before
/// invoking the callback. At most 128 distinct keys and 128 pending updates are admitted per activation.
/// Callbacks run asynchronously and must await all their storage work; this service does not serialize
/// callbacks. The caller owns ordering and coalescing. A successful newer update for the same key may
/// supersede a failed logical update, but cannot clear an uncertain underlying storage mutation.
/// Normal replacement and shutdown request settlement and await all accepted callbacks. Revocation
/// remains immediate and grants no post-revocation storage authority. Observe every returned task.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.AppStateUpdatesV1)]
public interface IPackageAppStateUpdates
{
    /// <summary>Atomically admits work for a portable storage key and returns its actual completion.</summary>
    /// <param name="key">Stable logical document key; a later admitted update replaces only this key's logical failure status.</param>
    /// <param name="update">Work which finishes with a successfully acknowledged save or throws its actual failure.</param>
    /// <param name="cancellationToken">Cancels this update, without treating cancellation as proof that a storage write was abandoned.</param>
    Task RunAsync(string key, Func<PackageAppStateUpdateContext, Task> update, CancellationToken cancellationToken = default);
}

/// <summary>Original-activation signals for one accepted App state update.</summary>
/// <param name="SettlementRequested">Requests an immediate flush instead of further debounce; this is not cancellation of the save.</param>
/// <param name="CancellationToken">Cancels work on explicit caller cancellation or original-activation revocation.</param>
[SunderSdkCapability(SunderSdkCapabilities.AppStateUpdatesV1)]
public sealed record PackageAppStateUpdateContext(CancellationToken SettlementRequested, CancellationToken CancellationToken);
