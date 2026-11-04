using System.Text.Json;
using Avalonia.Controls;

namespace Sunder.App.Bridge;

/// <summary>The shared managed App entry point. Implemented by generated package startup.</summary>
public interface IAppPackageEntryPointV1
{
    ValueTask<IAppPackageActivationV1> CreateAsync(IAppPackageHostV1 host, IAppContributionSinkV1 contributions);
}

/// <summary>Package-owned services. Start is called only after the App publishes the activation.</summary>
public interface IAppPackageActivationV1 : IAsyncDisposable
{
    void Start(CancellationToken revocationToken);
}

/// <summary>
/// Activation-scoped App authority. Identity is supplied by the App, never by an operation payload.
/// JSON operations are the versioned App V1 service contract, independent of SDK CLR types.
/// </summary>
public interface IAppPackageHostV1 : IAppServiceChannelV1
{
    string PackageId { get; }
    string PackageVersion { get; }
    string ContentRootPath { get; }
    Task RunStateUpdateAsync(string key, Func<CancellationToken, CancellationToken, Task> update, CancellationToken cancellationToken);
    JsonElement QueueBackgroundTask(JsonElement request, Func<AppBackgroundTaskContextV1, Task> execute);
    IDisposable SubscribeBackgroundTasks(Action<JsonElement> changed);
}

/// <summary>
/// Scoped service transport. Child channels own RPC scopes, development sessions or content leases.
/// Disposing a child releases that exact authority. Streams remain caller-owned; writes never dispose inputs.
/// Get is limited to synchronous local operations. Async operations must not be synchronously blocked.
/// </summary>
public interface IAppServiceChannelV1 : IAsyncDisposable
{
    JsonElement Get(string operation, JsonElement arguments);
    ValueTask<JsonElement> InvokeAsync(string operation, JsonElement arguments, CancellationToken cancellationToken);
    IAsyncEnumerable<JsonElement> WatchAsync(string operation, JsonElement arguments, CancellationToken cancellationToken);
    ValueTask<IAppServiceChannelV1> OpenAsync(string operation, JsonElement arguments, CancellationToken cancellationToken);
    ValueTask<Stream?> OpenReadAsync(string operation, JsonElement arguments, CancellationToken cancellationToken);
    ValueTask<JsonElement> WriteAsync(string operation, JsonElement arguments, Stream content, CancellationToken cancellationToken);
}

public sealed record AppBackgroundTaskContextV1(
    CancellationToken CancellationToken,
    Action<string> ReportStatus,
    Action<double, string?> ReportProgress,
    Action<string> ReportIndeterminate);

/// <summary>SDK exceptions are encoded at the boundary and reconstructed by the package's SDK.</summary>
public sealed class AppServiceExceptionV1(string domain, JsonElement error, string message) : Exception(message)
{
    public string Domain { get; } = domain;
    public JsonElement Error { get; } = error.Clone();
    public bool HostAuthenticated { get; }
    internal AppServiceExceptionV1(string domain, JsonElement error, string message, bool hostAuthenticated)
        : this(domain, error, message) => HostAuthenticated = hostAuthenticated;
}

public interface IAppContributionSinkV1
{
    void RegisterView(AppViewRegistrationV1 registration, Func<Control> factory);
    void RegisterSettingsView(Func<Control> factory);
}

public sealed record AppViewRegistrationV1(
    string Id, string Name, string? IconAssetPath, int DefaultPlacement, bool ShowInHotbarByDefault);
