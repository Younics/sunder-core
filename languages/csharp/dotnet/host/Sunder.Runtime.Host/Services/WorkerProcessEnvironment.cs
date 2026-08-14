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
        bool isWindows,
        bool allowDockerEnvironment)
    {
        ArgumentNullException.ThrowIfNull(readVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        var environment = new Dictionary<string, string?>(
            isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        Copy("PATH");
        if (isWindows)
        {
            Copy("USERPROFILE");
            Copy("PATHEXT");
            Copy("ComSpec");
            Copy("SystemRoot");
            if (!environment.ContainsKey("PATHEXT"))
            {
                environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
            }
            if (!environment.ContainsKey("ComSpec")
                && environment.TryGetValue("SystemRoot", out var systemRoot))
            {
                environment["ComSpec"] = Path.Combine(systemRoot!, "System32", "cmd.exe");
            }
        }
        else
        {
            Copy("HOME");
            Copy("LANG");
            Copy("LC_ALL");
            Copy("LC_CTYPE");
            if (!environment.ContainsKey("LANG"))
            {
                environment["LANG"] = "C.UTF-8";
            }
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
