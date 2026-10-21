using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageStateUpdates(AppPackageGenerationPublication publication) : IPackageAppStateUpdates
{
    public Task RunAsync(string key, Func<PackageAppStateUpdateContext, Task> update, CancellationToken cancellationToken = default)
        => publication.RunStateUpdateAsync(key, update, cancellationToken);
}
