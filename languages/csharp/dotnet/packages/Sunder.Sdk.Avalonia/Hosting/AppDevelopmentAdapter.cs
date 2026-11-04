using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;

namespace Sunder.Sdk.Avalonia.Hosting;

internal sealed class AppDevelopmentAdapter(AppChannel channel) : IPackageDevelopmentService
{
    public async Task<IPackageDevelopmentSession> LoadAsync(string developmentFolder, bool watch, CancellationToken cancellationToken = default)
        => new Session(await channel.Open("development.load", new { developmentFolder, watch }, cancellationToken).ConfigureAwait(false));
    public Task<PackageDevelopmentLogs> GetLogsAsync(string packageId, PackageDevelopmentLogQuery query, CancellationToken cancellationToken = default)
        => channel.Call<PackageDevelopmentLogs>("development.logs", new { packageId, query }, cancellationToken).AsTask();

    private sealed class Session(AppChannel channel) : IPackageDevelopmentSession
    {
        public string PackageId => channel.Get<string>("development.package-id");
        public Task<PackageDevelopmentStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => channel.Call<PackageDevelopmentStatus>("development.status", null, cancellationToken).AsTask();
        public Task SetWatchAsync(bool watch, CancellationToken cancellationToken = default)
            => channel.Call("development.watch", new { watch }, cancellationToken).AsTask();
        public Task<PackageDevelopmentRevision> ReloadAsync(string operationId, string sourceContentIdentity, CancellationToken cancellationToken = default)
            => channel.Call<PackageDevelopmentRevision>("development.reload", new { operationId, sourceContentIdentity }, cancellationToken).AsTask();
        public Task<IReadOnlyList<PackageDevelopmentView>> GetViewsAsync(CancellationToken cancellationToken = default)
            => channel.Call<IReadOnlyList<PackageDevelopmentView>>("development.views", null, cancellationToken).AsTask();
        public Task OpenViewAsync(string viewId, CancellationToken cancellationToken = default)
            => channel.Call("development.open-view", new { viewId }, cancellationToken).AsTask();
        public Task<PackageDevelopmentViewPage> QueryViewAsync(string viewId, PackageDevelopmentViewQuery query, CancellationToken cancellationToken = default)
            => channel.Call<PackageDevelopmentViewPage>("development.query-view", new { viewId, query }, cancellationToken).AsTask();
        public async Task<SunderContent> CaptureViewAsync(string viewId, PackageDevelopmentViewQuery scope, int maximumEdge = 1280, CancellationToken cancellationToken = default)
        {
            var content = await channel.Open("development.capture", new { viewId, scope, maximumEdge }, cancellationToken).ConfigureAwait(false);
            try
            {
                var metadata = content.Get<ContentMetadata>("content.metadata");
                return new(metadata.MediaType, metadata.FileName, metadata.Length, metadata.Sha256, metadata.ExpiresAtUtc, metadata.IsRepeatable,
                    async token => await content.Read("content.read", null, token).ConfigureAwait(false) ?? throw new InvalidDataException("Missing App content."), content.DisposeAsync);
            }
            catch { await content.DisposeAsync().ConfigureAwait(false); throw; }
        }
        public Task<PackageDevelopmentLogs> GetLogsAsync(int maximumEntries = 50, CancellationToken cancellationToken = default)
            => channel.Call<PackageDevelopmentLogs>("development.recent-logs", new { maximumEntries }, cancellationToken).AsTask();
        public Task<PackageDevelopmentLogs> GetLogsAsync(PackageDevelopmentLogQuery query, CancellationToken cancellationToken = default)
            => channel.Call<PackageDevelopmentLogs>("development.logs", new { query }, cancellationToken).AsTask();
        public Task<PackageDevelopmentViewSnapshot> InspectViewAsync(string viewId, CancellationToken cancellationToken = default)
            => channel.Call<PackageDevelopmentViewSnapshot>("development.inspect-view", new { viewId }, cancellationToken).AsTask();
        public Task<PackageDevelopmentViewInputResult> InteractViewAsync(PackageDevelopmentViewInput request, CancellationToken cancellationToken = default)
            => channel.Call<PackageDevelopmentViewInputResult>("development.interact-view", new { request }, cancellationToken).AsTask();
        public ValueTask DisposeAsync() => channel.DisposeAsync();
    }
    private sealed record ContentMetadata(string MediaType, string FileName, long? Length, string? Sha256, DateTimeOffset? ExpiresAtUtc, bool IsRepeatable);
}
