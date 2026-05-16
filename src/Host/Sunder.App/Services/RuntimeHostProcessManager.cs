using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using Sunder.App.Models;
using Sunder.Protocol;

namespace Sunder.App.Services;

public sealed class RuntimeHostProcessManager(AppStartupOptions startupOptions)
{
    private const string RuntimeHostName = "Sunder.Runtime.Host";

    private readonly AppStartupOptions _startupOptions = startupOptions;

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
        => await EnsureStartedAsync(_startupOptions.RuntimeUrl, cancellationToken);

    public async Task EnsureStartedAsync(Uri runtimeUrl, CancellationToken cancellationToken = default)
    {
        runtimeUrl = RuntimeUrlHelper.Normalize(runtimeUrl);

        var runtimeHostPath = ResolveRuntimeHostPath();
        var requiredRuntimeHostVersion = runtimeHostPath is null
            ? null
            : TryResolveRuntimeHostVersion(runtimeHostPath);

        var runningStatus = await TryGetRuntimeStatusAsync(runtimeUrl, cancellationToken);
        if (CanReuseRunningRuntime(runningStatus, requiredRuntimeHostVersion))
        {
            return;
        }

        if (ShouldReplaceRunningRuntime(runningStatus, requiredRuntimeHostVersion))
        {
            var runningVersion = runningStatus!.Version;
            AppSessionLog.WriteInfo(
                $"Replacing running Sunder.Runtime.Host {runningVersion} with bundled version {requiredRuntimeHostVersion}.");
            await ShutdownRuntimeAsync(runtimeUrl, cancellationToken);
            var stopped = await WaitForStoppedRuntimeAsync(runtimeUrl, cancellationToken);
            if (!stopped)
            {
                throw new InvalidOperationException(
                    $"Sunder.Runtime.Host {runningVersion} did not shut down in time to start bundled version {requiredRuntimeHostVersion}.");
            }
        }
        else if (runningStatus is not null || await IsRuntimeHealthyAsync(runtimeUrl, cancellationToken))
        {
            return;
        }

        if (runtimeHostPath is null)
        {
            throw new InvalidOperationException(
                "Unable to locate an installed Sunder.Runtime.Host next to Sunder.App. Use --runtime-host-path or SUNDER_RUNTIME_HOST_PATH to point at the runtime host executable or folder."
            );
        }

        Process.Start(CreateStartInfo(runtimeHostPath, runtimeUrl));

        var started = await WaitForAcceptableRuntimeAsync(runtimeUrl, requiredRuntimeHostVersion, cancellationToken);
        if (!started)
        {
            throw new InvalidOperationException($"Sunder.Runtime.Host did not become healthy at '{runtimeUrl}' in time.");
        }
    }

    private ProcessStartInfo CreateStartInfo(string runtimeHostPath, Uri runtimeUrl)
    {
        var runtimeUrlText = runtimeUrl.ToString().TrimEnd('/');
        var isDotnetAssembly = string.Equals(Path.GetExtension(runtimeHostPath), ".dll", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo(isDotnetAssembly ? "dotnet" : runtimeHostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runtimeHostPath)!,
        };

        if (isDotnetAssembly)
        {
            startInfo.ArgumentList.Add(runtimeHostPath);
        }

        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(runtimeUrlText);
        return startInfo;
    }

    private async Task<bool> WaitForAcceptableRuntimeAsync(
        Uri runtimeUrl,
        string? requiredRuntimeHostVersion,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var status = await TryGetRuntimeStatusAsync(runtimeUrl, cancellationToken);
            if (status is not null
                && IsSunderRuntimeHost(status)
                && CanReuseRunningRuntime(status, requiredRuntimeHostVersion))
            {
                return true;
            }

            await Task.Delay(400, cancellationToken);
        }

        return false;
    }

    private async Task<bool> WaitForStoppedRuntimeAsync(Uri runtimeUrl, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!await IsRuntimeHealthyAsync(runtimeUrl, cancellationToken))
            {
                return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    private async Task<SystemStatusResponse?> TryGetRuntimeStatusAsync(
        Uri runtimeUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return await httpClient.GetFromJsonAsync<SystemStatusResponse>(
                new Uri(runtimeUrl, "api/system"),
                cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private async Task ShutdownRuntimeAsync(Uri runtimeUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var _ = await httpClient.PostAsync(
                new Uri(runtimeUrl, "api/system/shutdown"),
                content: null,
                cancellationToken);
        }
        catch
        {
            // Startup will verify that the old host actually stopped before launching the bundled host.
        }
    }

    private async Task<bool> IsRuntimeHealthyAsync(Uri runtimeUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await httpClient.GetAsync(new Uri(runtimeUrl, "health"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
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

        if (!IsSunderRuntimeHost(runningStatus) || string.IsNullOrWhiteSpace(requiredRuntimeHostVersion))
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
