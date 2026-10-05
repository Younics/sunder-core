using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using SharedDevPackageOwnerSession = Sunder.Runtime.Client.DevPackageOwnerSession;

namespace Sunder.App.Services;

public sealed class DevPackageOwnerSession : IAsyncDisposable
{
    private readonly SharedDevPackageOwnerSession _shared;

    public DevPackageOwnerSession(RuntimeClientTransport transport)
    {
        _shared = new SharedDevPackageOwnerSession(
            transport,
            "app",
            static (operation, exception) => AppSessionLog.WriteError(
                operation == "heartbeat"
                    ? "The App dev package owner heartbeat failed; the Runtime TTL will fence a lost App session."
                    : "Failed to release the App dev package owner lease during shutdown.",
                exception));
    }

    public Task<DevPackageOwnerLeaseResponse> AcquireAsync(
        IReadOnlyList<string> folders,
        bool watch,
        CancellationToken cancellationToken = default)
        => _shared.AcquireAsync(folders, watch, cancellationToken);

    public Task ReleaseAsync(CancellationToken cancellationToken = default)
        => _shared.ReleaseAsync(cancellationToken);

    public ValueTask DisposeAsync() => _shared.DisposeAsync();
}
