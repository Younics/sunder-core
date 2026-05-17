using System.Diagnostics;
using System.Reflection;
using Sunder.App.Models;
using Sunder.Protocol;

namespace Sunder.App.Services;

public sealed class RuntimeHostProcessManager
{
    private const string RuntimeHostName = "Sunder.Runtime.Host";
    private static readonly RuntimeHealthProbe DefaultHealthProbe = new();

    private readonly AppStartupOptions _startupOptions;
    private readonly Func<string?> _resolveRuntimeHostPath;
    private readonly Func<Uri, CancellationToken, Task<SystemStatusResponse?>> _tryGetRuntimeStatusAsync;
    private readonly Func<Uri, CancellationToken, Task<bool>> _isRuntimeHealthyAsync;
    private readonly Func<Uri, CancellationToken, Task> _shutdownRuntimeAsync;
    private readonly Action<ProcessStartInfo> _startProcess;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly SemaphoreSlim _startupSemaphore = new(1, 1);

    public RuntimeHostProcessManager(AppStartupOptions startupOptions)
        : this(startupOptions, null, null, null, null, null, null)
    {
    }

    internal RuntimeHostProcessManager(
        AppStartupOptions startupOptions,
        Func<string?>? resolveRuntimeHostPath = null,
        Func<Uri, CancellationToken, Task<SystemStatusResponse?>>? tryGetRuntimeStatusAsync = null,
        Func<Uri, CancellationToken, Task<bool>>? isRuntimeHealthyAsync = null,
        Func<Uri, CancellationToken, Task>? shutdownRuntimeAsync = null,
        Action<ProcessStartInfo>? startProcess = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _startupOptions = startupOptions;
        _resolveRuntimeHostPath = resolveRuntimeHostPath ?? ResolveRuntimeHostPath;
        _tryGetRuntimeStatusAsync = tryGetRuntimeStatusAsync ?? DefaultHealthProbe.TryGetRuntimeStatusAsync;
        _isRuntimeHealthyAsync = isRuntimeHealthyAsync ?? DefaultHealthProbe.IsRuntimeHealthyAsync;
        _shutdownRuntimeAsync = shutdownRuntimeAsync ?? DefaultHealthProbe.ShutdownRuntimeAsync;
        _startProcess = startProcess ?? StartProcess;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
        => await EnsureStartedAsync(_startupOptions.RuntimeUrl, cancellationToken);

    public async Task EnsureStartedAsync(Uri runtimeUrl, CancellationToken cancellationToken = default)
    {
        runtimeUrl = RuntimeUrlHelper.Normalize(runtimeUrl);
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
        var requiredRuntimeHostVersion = runtimeHostPath is null
            ? null
            : TryResolveRuntimeHostVersion(runtimeHostPath);

        var runningStatus = await _tryGetRuntimeStatusAsync(runtimeUrl, cancellationToken);
        if (CanReuseRunningRuntime(runningStatus, requiredRuntimeHostVersion))
        {
            return;
        }

        if (ShouldReplaceRunningRuntime(runningStatus, requiredRuntimeHostVersion))
        {
            var runningVersion = runningStatus!.Version;
            AppSessionLog.WriteInfo(
                $"Replacing running Sunder.Runtime.Host {runningVersion} with bundled version {requiredRuntimeHostVersion}.");
            await _shutdownRuntimeAsync(runtimeUrl, cancellationToken);
            var stopped = await WaitForStoppedRuntimeAsync(runtimeUrl, cancellationToken);
            if (!stopped)
            {
                throw new InvalidOperationException(
                    $"Sunder.Runtime.Host {runningVersion} did not shut down in time to start bundled version {requiredRuntimeHostVersion}.");
            }
        }
        else if (runningStatus is not null)
        {
            throw CreateRuntimeUrlOccupiedException(runtimeUrl, runningStatus.Name);
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

        StartRuntimeHostProcess(RuntimeHostStartInfoFactory.Create(runtimeHostPath, runtimeUrl));

        var started = await WaitForAcceptableRuntimeAsync(runtimeUrl, requiredRuntimeHostVersion, cancellationToken);
        if (!started)
        {
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
        string? requiredRuntimeHostVersion,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var status = await _tryGetRuntimeStatusAsync(runtimeUrl, cancellationToken);
            if (status is not null
                && IsSunderRuntimeHost(status)
                && CanReuseRunningRuntime(status, requiredRuntimeHostVersion))
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
        SystemStatusResponse? runningStatus,
        string? requiredRuntimeHostVersion)
    {
        if (runningStatus is null)
        {
            return false;
        }

        if (!IsSunderRuntimeHost(runningStatus))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(requiredRuntimeHostVersion))
        {
            return true;
        }

        return RuntimeHostVersionComparer.TryCompare(
            runningStatus.Version,
            requiredRuntimeHostVersion,
            out var comparison)
            ? comparison >= 0
            : true;
    }

    internal static bool ShouldReplaceRunningRuntime(
        SystemStatusResponse? runningStatus,
        string? requiredRuntimeHostVersion)
    {
        if (runningStatus is null
            || !IsSunderRuntimeHost(runningStatus)
            || string.IsNullOrWhiteSpace(requiredRuntimeHostVersion))
        {
            return false;
        }

        return RuntimeHostVersionComparer.TryCompare(
            runningStatus.Version,
            requiredRuntimeHostVersion,
            out var comparison)
            && comparison < 0;
    }

    internal static string? TryResolveRuntimeHostVersion(string runtimeHostPath)
    {
        try
        {
            var assemblyPath = ResolveRuntimeHostAssemblyPath(runtimeHostPath);
            if (assemblyPath is null)
            {
                return null;
            }

            var versionInfo = FileVersionInfo.GetVersionInfo(assemblyPath);
            if (!string.IsNullOrWhiteSpace(versionInfo.ProductVersion))
            {
                return versionInfo.ProductVersion.Trim();
            }

            if (!string.IsNullOrWhiteSpace(versionInfo.FileVersion))
            {
                return versionInfo.FileVersion.Trim();
            }

            return AssemblyName.GetAssemblyName(assemblyPath).Version?.ToString(3);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveRuntimeHostAssemblyPath(string runtimeHostPath)
    {
        if (string.Equals(Path.GetExtension(runtimeHostPath), ".dll", StringComparison.OrdinalIgnoreCase)
            && File.Exists(runtimeHostPath))
        {
            return runtimeHostPath;
        }

        var runtimeHostDirectory = Path.GetDirectoryName(runtimeHostPath);
        if (!string.IsNullOrWhiteSpace(runtimeHostDirectory))
        {
            var assemblyPath = Path.Combine(runtimeHostDirectory, "Sunder.Runtime.Host.dll");
            if (File.Exists(assemblyPath))
            {
                return assemblyPath;
            }
        }

        return File.Exists(runtimeHostPath) ? runtimeHostPath : null;
    }

    private static bool IsSunderRuntimeHost(SystemStatusResponse status)
        => string.Equals(status.Name, RuntimeHostName, StringComparison.OrdinalIgnoreCase);

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
