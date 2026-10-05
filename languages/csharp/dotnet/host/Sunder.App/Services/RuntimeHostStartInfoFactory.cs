using System.Diagnostics;
using Sunder.Host.Contracts;
using Sunder.Runtime.Client;

namespace Sunder.App.Services;

internal static class RuntimeHostStartInfoFactory
{
    private static readonly HashSet<string> ManagedSupervisorEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUNDER_HOST_RUNTIME_PATH",
        "SUNDER_HOST_STATE_ROOT",
        "SUNDER_RUNTIME_STATE_ROOT",
        "SUNDER_RUNTIME_DEVELOPMENT_RELEASE_KEY_ID",
        "SUNDER_RUNTIME_DEVELOPMENT_RELEASE_PUBLIC_KEY_BASE64",
        "SUNDER_RUNTIME_DEVELOPMENT_TRUST_GENERATION",
        "SUNDER_RUNTIME_DEVELOPMENT_ALLOW_INSECURE_LOOPBACK_REGISTRY",
    };

    public static ProcessStartInfo Create(
        string runtimeHostPath,
        Uri runtimeUrl,
        string connectionInfoPath,
        bool managedSupervisor = false,
        string? deploymentIdentity = null,
        string? runtimeResourcePolicyFilePath = null)
    {
        var runtimeUrlText = runtimeUrl.ToString().TrimEnd('/');
        var isDotnetAssembly = string.Equals(Path.GetExtension(runtimeHostPath), ".dll", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo(isDotnetAssembly ? "dotnet" : runtimeHostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runtimeHostPath)!,
        };
        startInfo.Environment.Remove("SUNDER_EXECUTION_RUNTIME_DEVELOPMENT_STATE_ROOT");
        if (managedSupervisor)
        {
            SanitizeManagedSupervisorEnvironment(startInfo.Environment);
            if (deploymentIdentity is not null)
            {
                startInfo.Environment.Remove("SUNDER_HOST_RUNTIME_PATH");
            }
        }
        startInfo.Environment["SUNDER_RUNTIME_CONNECTION_FILE"] = connectionInfoPath;
        if (managedSupervisor && !string.IsNullOrWhiteSpace(runtimeResourcePolicyFilePath))
        {
            if (!Path.IsPathFullyQualified(runtimeResourcePolicyFilePath))
            {
                throw new ArgumentException(
                    "The Runtime resource policy file path must be absolute.",
                    nameof(runtimeResourcePolicyFilePath));
            }
            startInfo.Environment[RuntimeResourcePolicyEnvironment.PolicyFilePathVariable] =
                Path.GetFullPath(runtimeResourcePolicyFilePath);
        }

        if (isDotnetAssembly)
        {
            startInfo.ArgumentList.Add(runtimeHostPath);
        }

        if (managedSupervisor && deploymentIdentity is not null)
        {
            if (!HostDeploymentIdentity.IsValid(deploymentIdentity))
            {
                throw new ArgumentException(
                    "The Host deployment identity is not canonical.",
                    nameof(deploymentIdentity));
            }
            startInfo.ArgumentList.Add("--deployment-identity");
            startInfo.ArgumentList.Add(deploymentIdentity);
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
