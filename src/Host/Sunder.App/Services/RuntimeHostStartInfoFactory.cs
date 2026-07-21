using System.Diagnostics;
using Sunder.Runtime.Client;

namespace Sunder.App.Services;

internal static class RuntimeHostStartInfoFactory
{
    private static readonly HashSet<string> ManagedSupervisorEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUNDER_HOST_RUNTIME_PATH",
        "SUNDER_HOST_STATE_ROOT",
        "SUNDER_RUNTIME_STATE_ROOT",
    };

    public static ProcessStartInfo Create(
        string runtimeHostPath,
        Uri runtimeUrl,
        string connectionInfoPath,
        bool managedSupervisor = false)
    {
        var runtimeUrlText = runtimeUrl.ToString().TrimEnd('/');
        var isDotnetAssembly = string.Equals(Path.GetExtension(runtimeHostPath), ".dll", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo(isDotnetAssembly ? "dotnet" : runtimeHostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runtimeHostPath)!,
        };
        if (managedSupervisor)
        {
            SanitizeManagedSupervisorEnvironment(startInfo.Environment);
        }
        startInfo.Environment["SUNDER_RUNTIME_CONNECTION_FILE"] = connectionInfoPath;

        if (isDotnetAssembly)
        {
            startInfo.ArgumentList.Add(runtimeHostPath);
        }

        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(runtimeUrlText);
        return startInfo;
    }

    internal static void SanitizeManagedSupervisorEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var variable in environment.Keys
                     .Where(static variable => variable.StartsWith("SUNDER_", StringComparison.OrdinalIgnoreCase)
                                                && !ManagedSupervisorEnvironment.Contains(variable))
                     .ToArray())
        {
            environment.Remove(variable);
        }
    }
}
