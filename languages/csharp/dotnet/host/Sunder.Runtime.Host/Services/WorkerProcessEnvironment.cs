using Sunder.Package.Format;
using Sunder.Sdk.Compatibility;

namespace Sunder.Runtime.Host.Services;

internal static class WorkerProcessEnvironment
{
    private static readonly string[] DockerVariables =
    [
        "DOCKER_HOST",
        "DOCKER_CONTEXT",
        "DOCKER_CONFIG",
        "DOCKER_CERT_PATH",
        "DOCKER_TLS_VERIFY",
    ];

    public static IReadOnlyDictionary<string, string?> CreateBaseline(
        Func<string, string?> readVariable,
        string temporaryPath,
        WorkerProcessPlatform platform,
        bool allowDockerEnvironment)
    {
        ArgumentNullException.ThrowIfNull(readVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        var environment = new Dictionary<string, string?>(
            platform == WorkerProcessPlatform.Windows
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        if (platform == WorkerProcessPlatform.Windows)
        {
            var systemRoot = readVariable("SystemRoot");
            if (string.IsNullOrWhiteSpace(systemRoot))
            {
                systemRoot = readVariable("WINDIR");
            }
            if (string.IsNullOrWhiteSpace(systemRoot))
            {
                throw new InvalidOperationException(
                    "The Windows Worker environment requires SystemRoot or WINDIR.");
            }
            environment["SystemRoot"] = systemRoot;
            environment["WINDIR"] = systemRoot;
        }
        else if (platform == WorkerProcessPlatform.MacOS)
        {
            Copy("__CF_USER_TEXT_ENCODING");
        }
        else if (platform != WorkerProcessPlatform.Linux)
        {
            throw new ArgumentOutOfRangeException(nameof(platform), platform, "The Worker platform is unsupported.");
        }

        if (allowDockerEnvironment)
        {
            foreach (var variable in DockerVariables)
            {
                Copy(variable);
            }
        }

        environment["TMPDIR"] = temporaryPath;
        environment["TMP"] = temporaryPath;
        environment["TEMP"] = temporaryPath;
        return environment;

        void Copy(string name)
        {
            var value = readVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                environment[name] = value;
            }
        }
    }

    public static WorkerProcessPlatform GetCurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return WorkerProcessPlatform.Windows;
        if (OperatingSystem.IsMacOS()) return WorkerProcessPlatform.MacOS;
        if (OperatingSystem.IsLinux()) return WorkerProcessPlatform.Linux;
        throw new PlatformNotSupportedException("Sunder Workers require Windows, macOS, or Linux.");
    }

    public static bool AllowsDockerEnvironment(
        SunderPackageTargetManifest target,
        IReadOnlyList<string> approvedSensitiveCapabilities)
        => target.RequiredHostCapabilities?.Contains(
            SunderSdkCapabilities.DockerEnvironmentV1,
            StringComparer.Ordinal) == true
           && approvedSensitiveCapabilities.Contains(
               SunderSdkCapabilities.DockerEnvironmentV1,
               StringComparer.Ordinal);
}

internal enum WorkerProcessPlatform
{
    Windows,
    MacOS,
    Linux,
}
