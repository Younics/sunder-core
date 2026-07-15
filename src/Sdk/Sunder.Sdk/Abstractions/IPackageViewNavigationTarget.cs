using System.Collections.ObjectModel;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Receives navigation whenever an App-owned package view is initially presented, restored, directly selected, or opened programmatically.</summary>
/// <remarks>The host resolves the view or data-context target and enters the callback on the App UI dispatcher. A later navigation or closing the view cancels the current call. Successful completion tells the host that the view has reached a stable initial presentation for that navigation.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.ShellViewV1)]
public interface IPackageViewNavigationTarget
{
    /// <summary>Applies an immutable navigation snapshot.</summary>
    /// <remarks>The host resolves the target and invokes this method on the App UI dispatcher, so synchronous callback entry may safely inspect the Avalonia view. The cancellation token is cancelled when a later navigation supersedes this one or the view closes. Return only after the view has reached a stable initial presentation; continuing background work must not delay completion. Implementations that opt out of the captured context are responsible for dispatching later UI access.</remarks>
    ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides immutable navigation data for one package view.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ShellViewV1)]
public sealed record PackageViewNavigationContext
{
    /// <summary>Creates an immutable navigation snapshot.</summary>
    /// <param name="viewId">Stable globally unique id of the target view.</param>
    /// <param name="parameters">Case-sensitive parameter values to copy into an immutable snapshot; individual values may be <see langword="null"/>.</param>
    public PackageViewNavigationContext(string viewId, IReadOnlyDictionary<string, string?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ViewId = viewId;
        Parameters = new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(parameters, StringComparer.Ordinal));
    }

    /// <summary>Gets the stable globally unique id of the target view.</summary>
    public string ViewId { get; }

    /// <summary>Gets immutable case-sensitive parameter values.</summary>
    public IReadOnlyDictionary<string, string?> Parameters { get; }
}
