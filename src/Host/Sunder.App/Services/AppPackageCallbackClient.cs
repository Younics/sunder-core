using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;

namespace Sunder.App.Services;

internal sealed class AppPackageCallbackClient(
    string packageId,
    RuntimePackageCallbackClient client,
    ExternalBrowserService browser,
    AppPackageGenerationPublication? publication = null) : IPackageCallbackClient
{
    public bool IsAvailable => publication?.IsPublished ?? true;

    public async ValueTask<PackageCallbackSessionStatus> StartAsync(
        string callbackHandlerId,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackHandlerId);
        publication?.RequirePublished("callback sessions");
        var snapshot = PackageCallbackParameters.CopyAndValidate(parameters);
        return Map(await client.StartAsync(
            packageId,
            callbackHandlerId,
            snapshot,
            cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<PackageCallbackSessionStatus> GetStatusAsync(
        string callbackSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackSessionId);
        publication?.RequirePublished("callback sessions");
        return Map(await client.GetStatusAsync(packageId, callbackSessionId, cancellationToken).ConfigureAwait(false));
    }

    public ValueTask OpenLaunchUriAsync(Uri launchUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchUri);
        if (!launchUri.IsAbsoluteUri) throw new ArgumentException("The launch URI must be absolute.", nameof(launchUri));
        cancellationToken.ThrowIfCancellationRequested();
        publication?.RequirePublished("callback URI launching");
        browser.Open(launchUri);
        return ValueTask.CompletedTask;
    }

    private static PackageCallbackSessionStatus Map(PackageCallbackSessionResponse response)
        => new(
            response.PackageId,
            response.CallbackHandlerId,
            response.CallbackSessionId,
            response.State switch
            {
                Sunder.Runtime.Contracts.PackageCallbackSessionState.Pending => Sunder.Sdk.Callbacks.PackageCallbackSessionState.Pending,
                Sunder.Runtime.Contracts.PackageCallbackSessionState.Completed => Sunder.Sdk.Callbacks.PackageCallbackSessionState.Completed,
                Sunder.Runtime.Contracts.PackageCallbackSessionState.Cancelled => Sunder.Sdk.Callbacks.PackageCallbackSessionState.Cancelled,
                Sunder.Runtime.Contracts.PackageCallbackSessionState.Expired => Sunder.Sdk.Callbacks.PackageCallbackSessionState.Expired,
                _ => Sunder.Sdk.Callbacks.PackageCallbackSessionState.Failed,
            },
            response.Message,
            string.IsNullOrWhiteSpace(response.LaunchUri) ? null : new Uri(response.LaunchUri, UriKind.Absolute),
            response.ExpiresAtUtc);
}
