using Sunder.App.Bridge;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Logging;

namespace Sunder.Sdk.Avalonia.Hosting;

internal sealed class AppContextAdapter(IAppPackageHostV1 host, AppChannel channel) : IPackageContext
{
    public string PackageId => host.PackageId;
    public string Version => host.PackageVersion;
    public string ContentRootPath => host.ContentRootPath;
    public SunderDevelopmentToolchain DevelopmentToolchain => channel.Get<SunderDevelopmentToolchain>("toolchain");
    public IPackageStorageContext Storage { get; } = new StorageAdapter(channel);
    public IPackageSettings Settings { get; } = new SettingsAdapter(channel);
    public IPackageSecrets Secrets { get; } = new SecretsAdapter(channel);
    public IPackageCallbackClient Callbacks { get; } = new CallbackAdapter(channel);
    public IPackageLogging Logging { get; } = new AppLoggingAdapter(channel);

    private sealed class StorageAdapter(AppChannel channel) : IPackageStorageContext
    {
        public IPackageFileStore Files { get; } = new FilesAdapter(channel);
        public IPackageKeyValueStore State { get; } = new StateAdapter(channel);
        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; } = new WorkspaceAdapter(channel);
    }

    private sealed class WorkspaceAdapter(AppChannel channel) : IPackageRoleLocalWorkspace
    {
        public string WorkspaceRootPath => channel.Get<string>("workspace.root");
        public string GetLocalPath(string relativePath) => channel.Get<string>("workspace.path", new { relativePath });
    }

    private sealed class FilesAdapter(AppChannel channel) : IPackageFileStore
    {
        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
            => channel.Call<byte[]?>("files.read", new { relativePath }, cancellationToken).AsTask();
        public ValueTask<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
            => channel.Read("files.read", new { relativePath }, cancellationToken);
        public async Task WriteAsync(string relativePath, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
        {
            using var stream = new MemoryStream(contents.ToArray(), writable: false);
            await WriteAsync(relativePath, stream, cancellationToken).ConfigureAwait(false);
        }
        public async Task WriteAsync(string relativePath, Stream contents, CancellationToken cancellationToken = default)
            => await channel.Write<object?>("files.write", new { relativePath }, contents, cancellationToken).ConfigureAwait(false);
        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
            => channel.Call("files.delete", new { relativePath }, cancellationToken).AsTask();
    }

    private sealed class StateAdapter(AppChannel channel) : IPackageKeyValueStore
    {
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => channel.Call<string?>("state.get", new { key }, cancellationToken).AsTask();
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) => channel.Call("state.set", new { key, value }, cancellationToken).AsTask();
        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default) => channel.Call<bool>("state.contains", new { key }, cancellationToken).AsTask();
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default) => channel.Call("state.delete", new { key }, cancellationToken).AsTask();
        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default) => channel.Call<IReadOnlyList<string>>("state.list", new { prefix }, cancellationToken).AsTask();
    }

    private sealed class SettingsAdapter(AppChannel channel) : IPackageSettings
    {
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => channel.Call<string?>("settings.get", new { key }, cancellationToken).AsTask();
        public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default) => channel.Call<string?>("settings.stored", new { key }, cancellationToken).AsTask();
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) => channel.Call("settings.set", new { key, value }, cancellationToken).AsTask();
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default) => channel.Call("settings.delete", new { key }, cancellationToken).AsTask();
    }

    private sealed class SecretsAdapter(AppChannel channel) : IPackageSecrets
    {
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) => channel.Call<string?>("secrets.get", new { key }, cancellationToken).AsTask();
        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default) => channel.Call("secrets.set", new { key, value }, cancellationToken).AsTask();
        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default) => channel.Call("secrets.delete", new { key }, cancellationToken).AsTask();
    }

    private sealed class CallbackAdapter(AppChannel channel) : IPackageCallbackClient
    {
        public bool IsAvailable => channel.Get<bool>("callbacks.available");
        public ValueTask<PackageCallbackSessionStatus> StartAsync(string callbackHandlerId, IReadOnlyDictionary<string, string>? parameters = null, CancellationToken cancellationToken = default)
            => channel.Call<PackageCallbackSessionStatus>("callbacks.start", new { callbackHandlerId, parameters }, cancellationToken);
        public ValueTask<PackageCallbackSessionStatus> GetStatusAsync(string callbackSessionId, CancellationToken cancellationToken = default)
            => channel.Call<PackageCallbackSessionStatus>("callbacks.status", new { callbackSessionId }, cancellationToken);
        public ValueTask<bool> CancelAsync(string callbackSessionId, CancellationToken cancellationToken = default)
            => channel.Call<bool>("callbacks.cancel", new { callbackSessionId }, cancellationToken);
        public ValueTask<PackageCallbackSessionStatus> WaitForCompletionAsync(string callbackSessionId, TimeSpan timeout, CancellationToken cancellationToken = default)
            => channel.Call<PackageCallbackSessionStatus>("callbacks.wait", new { callbackSessionId, timeout }, cancellationToken);
        public ValueTask OpenLaunchUriAsync(Uri launchUri, CancellationToken cancellationToken = default)
            => channel.Call("callbacks.launch", new { launchUri }, cancellationToken);
    }
}
