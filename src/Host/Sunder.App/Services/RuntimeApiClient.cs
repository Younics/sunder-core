using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed partial class RuntimeApiClient : IRuntimeApiClient, IRuntimeRpcManagementClient
{
    private readonly RuntimeClientTransport? _ownedTransport;
    private readonly RuntimeManagementClient _management;

    public RuntimeApiClient(Uri runtimeBaseUri)
        : this(() => RuntimeConnectionInfoStore.LoadFor(runtimeBaseUri)) { }

    public RuntimeApiClient(RuntimeConnectionState runtimeConnectionState)
        : this(() => runtimeConnectionState.ConnectionInfo) { }

    public RuntimeApiClient(RuntimeClientTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _management = new RuntimeManagementClient(transport);
    }

    internal RuntimeApiClient(
        Func<RuntimeConnectionInfo?> getConnectionInfo,
        HttpMessageHandler? innerHandler = null)
    {
        ArgumentNullException.ThrowIfNull(getConnectionInfo);
        if (innerHandler is null && getConnectionInfo.Target is RuntimeClientTransport sharedTransport)
        {
            _management = new RuntimeManagementClient(sharedTransport);
            return;
        }

        _ownedTransport = new RuntimeClientTransport(getConnectionInfo, innerHandler);
        _management = new RuntimeManagementClient(_ownedTransport);
    }

    public Task<PackageStoreStageResult> StagePackageStoreChangesAsync(
        PackageStoreStageRequest request,
        CancellationToken cancellationToken = default)
        => _management.StagePackageStoreChangesAsync(request, cancellationToken);

    public Task<PackageOperationResult> CommitPackageStoreStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
        => _management.CommitPackageStoreStageAsync(stageId, cancellationToken);

    public Task DiscardPackageStoreStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
        => _management.DiscardPackageStoreStageAsync(stageId, cancellationToken);

    public Task<RuntimePackageStageStatus> GetPackageStoreStageStatusAsync(
        string stageId,
        CancellationToken cancellationToken = default)
        => _management.GetPackageStageStatusAsync(stageId, cancellationToken);

    public void Dispose()
    {
        _management.Dispose();
        _ownedTransport?.Dispose();
    }
}
