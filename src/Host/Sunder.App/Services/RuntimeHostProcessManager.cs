using System.Diagnostics;
using Sunder.App.Models;

namespace Sunder.App.Services;

public sealed class RuntimeHostProcessManager(AppStartupOptions startupOptions)
{
    private readonly AppStartupOptions _startupOptions = startupOptions;

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
        => await EnsureStartedAsync(_startupOptions.RuntimeUrl, cancellationToken);

    public async Task EnsureStartedAsync(Uri runtimeUrl, CancellationToken cancellationToken = default)
    {
        runtimeUrl = RuntimeUrlHelper.Normalize(runtimeUrl);

        if (await IsRuntimeHealthyAsync(runtimeUrl, cancellationToken))
        {
            return;
        }

        var runtimeHostPath = ResolveRuntimeHostPath();
        if (runtimeHostPath is null)
        {
            throw new InvalidOperationException(
                "Unable to locate an installed Sunder.Runtime.Host next to Sunder.App. Use --runtime-host-path or SUNDER_RUNTIME_HOST_PATH to point at the runtime host executable or folder."
            );
        }

        Process.Start(CreateStartInfo(runtimeHostPath, runtimeUrl));

        var started = await WaitForHealthyRuntimeAsync(runtimeUrl, cancellationToken);
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

    private async Task<bool> WaitForHealthyRuntimeAsync(Uri runtimeUrl, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (await IsRuntimeHealthyAsync(runtimeUrl, cancellationToken))
            {
                return true;
            }

            await Task.Delay(400, cancellationToken);
        }

        return false;
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
