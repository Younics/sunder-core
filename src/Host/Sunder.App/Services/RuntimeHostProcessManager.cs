using System.Diagnostics;
using Sunder.App.Models;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed class RuntimeHostProcessManager : IDisposable
{
    private const string RuntimeHostName = "Sunder.Runtime.Host";
    internal static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(400);
    private readonly AppStartupOptions _startupOptions;
    private readonly RuntimeConnectionState _runtimeConnectionState;
    private readonly Func<string?> _resolveRuntimeHostPath;
    private readonly Func<Uri, CancellationToken, Task<RuntimeHandshakeResponse?>> _tryGetRuntimeHandshakeAsync;
    private readonly Func<Uri, CancellationToken, Task<bool>> _isRuntimeHealthyAsync;
    private readonly Func<Uri, CancellationToken, Task> _shutdownRuntimeAsync;
    private readonly Func<ProcessStartInfo, bool, CancellationToken, Task> _launchRuntimeAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly string _connectionInfoPath;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _startupTimeout;
    private readonly Func<bool> _isRuntimeLeaseAvailable;
    private readonly SemaphoreSlim _startupSemaphore = new(1, 1);
    private readonly RuntimeHealthProbe? _healthProbe;

    public RuntimeHostProcessManager(AppStartupOptions startupOptions)
        : this(startupOptions, null, null, null, null, null, null, null, null)
    {
    }

    public RuntimeHostProcessManager(
        AppStartupOptions startupOptions,
        RuntimeConnectionState runtimeConnectionState)
        : this(startupOptions, runtimeConnectionState, null, null, null, null, null, null, null, null)
    {
    }

    public RuntimeHostProcessManager(
        AppStartupOptions startupOptions,
        RuntimeConnectionState runtimeConnectionState,
        RuntimeClientTransport runtimeTransport)
        : this(startupOptions, runtimeConnectionState, null, null, null, null, null, null, null, runtimeTransport)
    {
    }

    internal RuntimeHostProcessManager(
        AppStartupOptions startupOptions,
        RuntimeConnectionState? runtimeConnectionState = null,
        Func<string?>? resolveRuntimeHostPath = null,
        Func<Uri, CancellationToken, Task<RuntimeHandshakeResponse?>>? tryGetRuntimeHandshakeAsync = null,
        Func<Uri, CancellationToken, Task<bool>>? isRuntimeHealthyAsync = null,
        Func<Uri, CancellationToken, Task>? shutdownRuntimeAsync = null,
        Action<ProcessStartInfo>? startProcess = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        string? connectionInfoPath = null,
        RuntimeClientTransport? runtimeTransport = null,
        TimeProvider? timeProvider = null,
        TimeSpan? startupTimeout = null,
        Func<bool>? isRuntimeLeaseAvailable = null)
    {
        _startupOptions = startupOptions;
        _runtimeConnectionState = runtimeConnectionState ?? new RuntimeConnectionState(startupOptions.RuntimeUrl);
        var healthProbe = tryGetRuntimeHandshakeAsync is null
                          || isRuntimeHealthyAsync is null
                          || shutdownRuntimeAsync is null
            ? new RuntimeHealthProbe(_runtimeConnectionState, runtimeTransport)
            : null;
        _healthProbe = healthProbe;
        _resolveRuntimeHostPath = resolveRuntimeHostPath ?? ResolveRuntimeHostPath;
        _tryGetRuntimeHandshakeAsync = tryGetRuntimeHandshakeAsync ?? healthProbe!.TryGetRuntimeHandshakeAsync;
        _isRuntimeHealthyAsync = isRuntimeHealthyAsync ?? healthProbe!.IsRuntimeHealthyAsync;
        _shutdownRuntimeAsync = shutdownRuntimeAsync ?? healthProbe!.ShutdownRuntimeAsync;
        var persistentLauncher = RuntimePersistentLauncher.Create();
        _launchRuntimeAsync = startProcess is null
            ? persistentLauncher.LaunchAsync
            : (startInfo, _, _) =>
            {
                startProcess(startInfo);
                return Task.CompletedTask;
            };
        _delayAsync = delayAsync ?? Task.Delay;
        _connectionInfoPath = connectionInfoPath ?? RuntimeConnectionInfoStore.GetDefaultPath();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startupTimeout = startupTimeout ?? DefaultStartupTimeout;
        _isRuntimeLeaseAvailable = isRuntimeLeaseAvailable ?? (() => RuntimeLocalState.IsLeaseAvailable());
        if (_startupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startupTimeout), "Runtime startup timeout must be positive.");
        }
    }

    public void Dispose()
    {
        _healthProbe?.Dispose();
        _startupSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
        => await EnsureStartedAsync(_startupOptions.RuntimeUrl, cancellationToken);

    public async Task EnsureStartedAsync(Uri runtimeUrl, CancellationToken cancellationToken = default)
    {
        runtimeUrl = RuntimeUrlHelper.Normalize(runtimeUrl);
        _runtimeConnectionState.RuntimeUrl = runtimeUrl;
        await _startupSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStartedCoreAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startupSemaphore.Release();
        }
    }

    private async Task EnsureStartedCoreAsync(Uri runtimeUrl, CancellationToken cancellationToken)
    {
        var runtimeHostPath = _resolveRuntimeHostPath();
        await RejectOrRemoveMismatchedPublishedRuntimeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
        RefreshPublishedConnection(runtimeUrl);
        var runningHandshake = await _tryGetRuntimeHandshakeAsync(runtimeUrl, cancellationToken);
        if (CanReuseRunningRuntime(runningHandshake))
        {
            return;
        }

        var replaceExistingRuntime = ShouldReplaceRunningRuntime(runningHandshake);
        if (replaceExistingRuntime)
        {
            var runningVersion = runningHandshake!.Product?.ProductVersion ?? "unknown";
            var reason = RuntimeProtocolCompatibility.GetIncompatibility(runningHandshake);
            AppSessionLog.WriteInfo($"Replacing Sunder.Runtime.Host {runningVersion}: {reason}");
            await _shutdownRuntimeAsync(runtimeUrl, cancellationToken);
            var stopped = await WaitForStoppedRuntimeAsync(runtimeUrl, cancellationToken);
            if (!stopped)
            {
                throw new InvalidOperationException(
                    $"Sunder.Runtime.Host {runningVersion} did not shut down in time to start the bundled compatible Runtime.");
            }
        }
        else if (runningHandshake is not null)
        {
            throw CreateRuntimeUrlOccupiedException(runtimeUrl, runningHandshake.Product?.ProductName);
        }
        else if (await _isRuntimeHealthyAsync(runtimeUrl, cancellationToken))
        {
            throw CreateRuntimeUrlOccupiedException(runtimeUrl, serviceName: null);
        }

        if (runtimeHostPath is null)
        {
            throw new InvalidOperationException(
                "Unable to locate an installed Sunder.Runtime.Host next to Sunder.App. Use --runtime-host-path or SUNDER_RUNTIME_HOST_PATH to point at the runtime host executable or folder."
            );
        }

        if (!runtimeUrl.IsLoopback)
        {
            throw new InvalidOperationException(
                $"The managed Runtime cannot be launched at non-loopback URL '{runtimeUrl}'. Connect only with matching authenticated connection information, or launch the Runtime manually with the explicit development-only non-loopback override.");
        }

        if (!_isRuntimeLeaseAvailable())
        {
            var stopped = await WaitForStoppedRuntimeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
            if (!stopped)
            {
                throw new InvalidOperationException(
                    "The previous Sunder.Runtime.Host is still releasing local state. Retry after it finishes shutting down.");
            }
            replaceExistingRuntime = true;
        }

        var staleConnection = RuntimeConnectionInfoStore.Load(_connectionInfoPath);
        if (staleConnection is not null)
        {
            RuntimeConnectionInfoStore.DeleteIfMatches(staleConnection, _connectionInfoPath);
        }
        _runtimeConnectionState.RuntimeUrl = runtimeUrl;
        var launchStopwatch = Stopwatch.StartNew();
        await StartRuntimeHostProcessAsync(RuntimeHostStartInfoFactory.Create(
            runtimeHostPath,
            runtimeUrl,
            _connectionInfoPath), replaceExistingRuntime, cancellationToken).ConfigureAwait(false);
        AppSessionLog.WriteInfo(
            $"Runtime launcher accepted '{runtimeUrl}' in {launchStopwatch.ElapsedMilliseconds} ms.");

        var readinessStopwatch = Stopwatch.StartNew();
        var started = await WaitForAcceptableRuntimeAsync(runtimeUrl, cancellationToken);
        if (!started)
        {
            _runtimeConnectionState.RuntimeUrl = runtimeUrl;
            throw new InvalidOperationException(
                $"Sunder.Runtime.Host did not publish a compatible authenticated connection at '{runtimeUrl}' within {_startupTimeout.TotalSeconds:0} seconds. The managed Runtime may still be starting; Retry will reuse it.");
        }
        AppSessionLog.WriteInfo(
            $"Runtime authenticated connection became available at '{runtimeUrl}' in {readinessStopwatch.ElapsedMilliseconds} ms.");
    }

    private async Task RejectOrRemoveMismatchedPublishedRuntimeAsync(
        Uri runtimeUrl,
        CancellationToken cancellationToken)
    {
        var published = RuntimeConnectionInfoStore.Load(_connectionInfoPath);
        if (published is null || published.Matches(runtimeUrl))
        {
            return;
        }

        _runtimeConnectionState.SetConnection(published);
        try
        {
            var handshake = await _tryGetRuntimeHandshakeAsync(published.RuntimeUrl, cancellationToken)
                .ConfigureAwait(false);
            if (handshake is not null
                || await _isRuntimeHealthyAsync(published.RuntimeUrl, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"A Runtime published at '{published.RuntimeUrl}' is still running. Stop it before starting the managed Runtime at '{runtimeUrl}'.");
            }

            if (!_isRuntimeLeaseAvailable())
            {
                var stopped = await WaitForStoppedRuntimeAsync(published.RuntimeUrl, cancellationToken)
                    .ConfigureAwait(false);
                if (!stopped)
                {
                    throw new InvalidOperationException(
                        $"The Runtime previously published at '{published.RuntimeUrl}' is still releasing local state. Retry after it finishes shutting down.");
                }
            }

            RuntimeConnectionInfoStore.DeleteIfMatches(published, _connectionInfoPath);
        }
        finally
        {
            _runtimeConnectionState.RuntimeUrl = runtimeUrl;
        }
    }

    private async Task StartRuntimeHostProcessAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        try
        {
            await _launchRuntimeAsync(startInfo, replaceExisting, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to start Sunder.Runtime.Host using '{startInfo.FileName}'.",
                ex);
        }
    }

    private async Task<bool> WaitForAcceptableRuntimeAsync(
        Uri runtimeUrl,
        CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetTimestamp();
        while (true)
        {
            RefreshPublishedConnection(runtimeUrl);
            var handshake = await _tryGetRuntimeHandshakeAsync(runtimeUrl, cancellationToken);
            if (CanReuseRunningRuntime(handshake))
            {
                return true;
            }

            var remaining = _startupTimeout - _timeProvider.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await _delayAsync(
                remaining < StartupPollInterval ? remaining : StartupPollInterval,
                cancellationToken);
        }
    }

    private async Task<bool> WaitForStoppedRuntimeAsync(Uri runtimeUrl, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (!await _isRuntimeHealthyAsync(runtimeUrl, cancellationToken)
                && _isRuntimeLeaseAvailable())
            {
                return true;
            }

            if (attempt < 119)
            {
                await _delayAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }

        return false;
    }

    private string? ResolveRuntimeHostPath()
    {
        return ResolveFromPath(_startupOptions.RuntimeHostPath)
            ?? ResolveFromPath(Path.Combine(AppContext.BaseDirectory, "RuntimeHost"))
            ?? ResolveFromPath(AppContext.BaseDirectory)
#if DEBUG
            ?? ResolveFromPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "Sunder.Runtime.Host",
                    "bin",
                    "Debug",
                    "net10.0"
                )
            )
#endif
            ;
    }

    internal static bool CanReuseRunningRuntime(
        RuntimeHandshakeResponse? handshake)
        => RuntimeProtocolCompatibility.IsCompatible(handshake);

    internal static bool ShouldReplaceRunningRuntime(
        RuntimeHandshakeResponse? handshake)
        => handshake is not null
           && string.Equals(handshake.ProtocolIdentity, RuntimeProtocol.Identity, StringComparison.Ordinal)
           && !RuntimeProtocolCompatibility.IsCompatible(handshake);

    private void RefreshPublishedConnection(Uri runtimeUrl)
    {
        var published = RuntimeConnectionInfoStore.Load(_connectionInfoPath);
        if (published is null
            || !published.Matches(runtimeUrl))
        {
            return;
        }

        _runtimeConnectionState.SetConnection(published);
    }

    private static InvalidOperationException CreateRuntimeUrlOccupiedException(Uri runtimeUrl, string? serviceName)
    {
        var serviceDescription = string.IsNullOrWhiteSpace(serviceName)
            ? "a service that does not identify as Sunder.Runtime.Host"
            : $"'{serviceName}'";

        return new InvalidOperationException(
            $"Runtime URL '{runtimeUrl}' is already occupied by {serviceDescription}. Stop that service or choose a different runtime URL.");
    }

    private static string? ResolveFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        if (!Directory.Exists(fullPath))
        {
            return null;
        }

        foreach (var candidate in GetRuntimeFileCandidates(fullPath))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetRuntimeFileCandidates(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(directory, "Sunder.Runtime.Host.exe");
        }
        else
        {
            yield return Path.Combine(directory, "Sunder.Runtime.Host");
        }

        yield return Path.Combine(directory, "Sunder.Runtime.Host.dll");
    }
}
