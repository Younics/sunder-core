using System.Collections.Concurrent;
using System.Diagnostics;

namespace Sunder.App.Services;

internal static class RuntimePersistentLauncher
{
    public static IHostServiceManager Create()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacOsSessionRuntimeLauncher();
        }
        if (OperatingSystem.IsLinux())
        {
            return new LinuxSystemdRuntimeLauncher();
        }
        if (OperatingSystem.IsWindows())
        {
            return new WindowsBreakawayRuntimeLauncher();
        }

        return new DirectRuntimeLauncher();
    }
}

internal abstract class RuntimePersistentLauncherBase : IHostServiceManager, IDisposable
{
    public Task<HostServiceLaunchReceipt> LaunchAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken)
        => ReconcileAndLaunchAsync(startInfo, replaceExisting, cancellationToken);

    public abstract Task<HostServiceLaunchReceipt> ReconcileAndLaunchAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken);

    public virtual Task<HostServiceObservation> ObserveAsync(
        HostServiceLaunchReceipt receipt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(HostServiceObservation.Unknown(receipt.ProcessId));
    }

    public virtual Task<bool> TryStopAsync(
        HostServiceLaunchReceipt receipt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public virtual void Dispose()
    {
    }
}

internal sealed class DirectRuntimeLauncher : RuntimePersistentLauncherBase
{
    private readonly ConcurrentDictionary<int, Process> _processes = new();

    public override Task<HostServiceLaunchReceipt> ReconcileAndLaunchAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeLauncherProcess.SanitizeServiceEnvironment(startInfo);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start returned null.");
        if (!_processes.TryAdd(process.Id, process))
        {
            process.Dispose();
            throw new InvalidOperationException("The direct Host process could not be tracked.");
        }

        return Task.FromResult(new HostServiceLaunchReceipt("direct", "sunder-host", process.Id));
    }

    public override Task<HostServiceObservation> ObserveAsync(
        HostServiceLaunchReceipt receipt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (receipt.ProcessId is not { } processId)
        {
            return Task.FromResult(HostServiceObservation.Unknown());
        }
        if (!_processes.TryGetValue(processId, out var process))
        {
            return Task.FromResult(new HostServiceObservation(
                HostServiceState.Absent,
                processId,
                result: "process-not-found"));
        }

        if (!process.HasExited)
        {
            return Task.FromResult(new HostServiceObservation(HostServiceState.Running, processId));
        }

        var exitCode = process.ExitCode;
        if (_processes.TryRemove(processId, out var completedProcess))
        {
            completedProcess.Dispose();
        }
        return Task.FromResult(new HostServiceObservation(
            exitCode == 0 ? HostServiceState.Stopped : HostServiceState.Failed,
            processId,
            exitCode,
            result: "process-exit",
            safeDetail: "The Host process exited before its Runtime connection became ready."));
    }

    public override async Task<bool> TryStopAsync(
        HostServiceLaunchReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (receipt.ProcessId is not { } processId
            || !_processes.TryGetValue(processId, out var process))
        {
            return false;
        }
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (_processes.TryRemove(processId, out var completed))
        {
            completed.Dispose();
        }
        return true;
    }

    public override void Dispose()
    {
        foreach (var pair in _processes)
        {
            if (_processes.TryRemove(pair.Key, out var process))
            {
                process.Dispose();
            }
        }
    }
}

internal static class RuntimeLauncherProcess
{
    private static readonly HostServiceCommandRunner CommandRunner = new();
    private static readonly HashSet<string> ExplicitEnvironmentVariables = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "SUNDER_RUNTIME_CONNECTION_FILE",
        "SUNDER_HOST_RUNTIME_PATH",
        "SUNDER_HOST_STATE_ROOT",
        "SUNDER_RUNTIME_STATE_ROOT",
        "DOTNET_ROOT",
        "PATH",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY",
        "XDG_DATA_HOME",
        "XDG_CONFIG_HOME",
        "XDG_CACHE_HOME",
    };
    private static readonly HashSet<string> SafeInheritedEnvironmentVariables = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "HOME",
        "USER",
        "LOGNAME",
        "TMPDIR",
        "TMP",
        "TEMP",
        "LANG",
        "LC_ALL",
        "TZ",
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
        "XDG_RUNTIME_DIR",
        "SystemRoot",
        "WINDIR",
        "ComSpec",
        "USERPROFILE",
        "LOCALAPPDATA",
        "APPDATA",
        "PROGRAMDATA",
        "PROGRAMFILES",
        "PROGRAMFILES(X86)",
        "COMMONPROGRAMFILES",
        "COMMONPROGRAMFILES(X86)",
        "PATHEXT",
        "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE",
    };

    public static IEnumerable<KeyValuePair<string, string>> GetExplicitEnvironment(ProcessStartInfo startInfo)
        => startInfo.Environment
            .Where(pair => pair.Value is not null && ExplicitEnvironmentVariables.Contains(pair.Key))
            .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value!));

    public static IEnumerable<KeyValuePair<string, string>> GetServiceEnvironment(ProcessStartInfo startInfo)
        => startInfo.Environment
            .Where(pair => pair.Value is not null
                           && (ExplicitEnvironmentVariables.Contains(pair.Key)
                               || SafeInheritedEnvironmentVariables.Contains(pair.Key)
                               || pair.Key.StartsWith("LC_", StringComparison.OrdinalIgnoreCase)))
            .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value!));

    public static void SanitizeServiceEnvironment(ProcessStartInfo startInfo)
    {
        var environment = GetServiceEnvironment(startInfo).ToArray();
        startInfo.Environment.Clear();
        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
    }

    public static string ResolveExecutable(string fileName)
    {
        if (Path.IsPathFullyQualified(fileName))
        {
            return fileName;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
                         && string.IsNullOrEmpty(Path.GetExtension(fileName))
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, fileName + extension));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return fileName;
    }

    public static async Task RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnFailure = true)
    {
        var result = await RunResultAsync(fileName, arguments, cancellationToken).ConfigureAwait(false);
        if (throwOnFailure && result.ExitCode != 0)
        {
            var diagnostic = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            throw new InvalidOperationException(
                $"Host service command '{HostServiceDiagnostics.SanitizeCommandName(fileName)}' exited with code {result.ExitCode}"
                + (string.IsNullOrWhiteSpace(diagnostic) ? "." : $": {diagnostic.Trim()}"));
        }
    }

    public static Task<HostServiceCommandResult> RunResultAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => CommandRunner.RunAsync(fileName, arguments, cancellationToken);
}
