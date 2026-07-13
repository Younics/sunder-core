using System.Diagnostics;
using Sunder.App.Models;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed class RuntimeHostProcessManager
{
    private const string RuntimeHostName = "Sunder.Runtime.Host";
    private readonly AppStartupOptions _startupOptions;
    private readonly RuntimeConnectionState _runtimeConnectionState;
    private readonly Func<string?> _resolveRuntimeHostPath;
    private readonly Func<Uri, CancellationToken, Task<RuntimeHandshakeResponse?>> _tryGetRuntimeHandshakeAsync;
    private readonly Func<Uri, CancellationToken, Task<bool>> _isRuntimeHealthyAsync;
    private readonly Func<Uri, CancellationToken, Task> _shutdownRuntimeAsync;
    private readonly Action<ProcessStartInfo> _startProcess;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly string _connectionInfoPath;
    private readonly SemaphoreSlim _startupSemaphore = new(1, 1);

    public RuntimeHostProcessManager(AppStartupOptions startupOptions)
        : this(startupOptions, null, null, null, null, null, null, null, null)
    {
    }

    public RuntimeHostProcessManager(
        AppStartupOptions startupOptions,
        RuntimeConnectionState runtimeConnectionState)
        : this(startupOptions, runtimeConnectionState, null, null, null, null, null, null, null)
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
        string? connectionInfoPath = null)
    {
        _startupOptions = startupOptions;
        _runtimeConnectionState = runtimeConnectionState ?? new RuntimeConnectionState(startupOptions.RuntimeUrl);
        var healthProbe = new RuntimeHealthProbe(_runtimeConnectionState);
        _resolveRuntimeHostPath = resolveRuntimeHostPath ?? ResolveRuntimeHostPath;
        _tryGetRuntimeHandshakeAsync = tryGetRuntimeHandshakeAsync ?? healthProbe.TryGetRuntimeHandshakeAsync;
        _isRuntimeHealthyAsync = isRuntimeHealthyAsync ?? healthProbe.IsRuntimeHealthyAsync;
        _shutdownRuntimeAsync = shutdownRuntimeAsync ?? healthProbe.ShutdownRuntimeAsync;
        _startProcess = startProcess ?? StartProcess;
        _delayAsync = delayAsync ?? Task.Delay;
        _connectionInfoPath = connectionInfoPath ?? RuntimeConnectionInfoStore.GetDefaultPath();
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
        var runningHandshake = await _tryGetRuntimeHandshakeAsync(runtimeUrl, cancellationToken);
        if (CanReuseRunningRuntime(runningHandshake))
        {
            return;
        }

        if (ShouldReplaceRunningRuntime(runningHandshake))
        {
            var runningVersion = runningHandshake!.Product?.ProductVersion ?? "unknown";
            AppSessionLog.WriteInfo(
                $"Replacing incompatible Sunder.Runtime.Host {runningVersion}: {RuntimeProtocolCompatibility.GetIncompatibility(runningHandshake)}");
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

        var connectionInfo = new RuntimeConnectionInfo(runtimeUrl, RuntimeBearerToken.Create());
        RuntimeConnectionInfoStore.Save(connectionInfo, _connectionInfoPath);
        _runtimeConnectionState.SetConnection(connectionInfo);
        try
        {
            StartRuntimeHostProcess(RuntimeHostStartInfoFactory.Create(
                runtimeHostPath,
                runtimeUrl,
                connectionInfo.BearerToken,
                _connectionInfoPath,
                _startupOptions.DevPackageFolders));
        }
        catch
        {
            RuntimeConnectionInfoStore.DeleteIfMatches(connectionInfo, _connectionInfoPath);
            throw;
        }

        var started = await WaitForAcceptableRuntimeAsync(runtimeUrl, cancellationToken);
        if (!started)
        {
            RuntimeConnectionInfoStore.DeleteIfMatches(connectionInfo, _connectionInfoPath);
            _runtimeConnectionState.RuntimeUrl = runtimeUrl;
            throw new InvalidOperationException($"Sunder.Runtime.Host did not become healthy at '{runtimeUrl}' in time.");
        }
    }

    private void StartRuntimeHostProcess(ProcessStartInfo startInfo)
    {
        try
        {
            _startProcess(startInfo);
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
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var handshake = await _tryGetRuntimeHandshakeAsync(runtimeUrl, cancellationToken);
            if (CanReuseRunningRuntime(handshake))
            {
                return true;
            }

            await _delayAsync(TimeSpan.FromMilliseconds(400), cancellationToken);
        }

        return false;
    }

    private async Task<bool> WaitForStoppedRuntimeAsync(Uri runtimeUrl, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!await _isRuntimeHealthyAsync(runtimeUrl, cancellationToken))
            {
                return true;
            }

            await _delayAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
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

    private static InvalidOperationException CreateRuntimeUrlOccupiedException(Uri runtimeUrl, string? serviceName)
    {
        var serviceDescription = string.IsNullOrWhiteSpace(serviceName)
            ? "a service that does not identify as Sunder.Runtime.Host"
            : $"'{serviceName}'";

        return new InvalidOperationException(
            $"Runtime URL '{runtimeUrl}' is already occupied by {serviceDescription}. Stop that service or choose a different runtime URL.");
    }

    private static void StartProcess(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start returned null.");
        process.Dispose();
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
