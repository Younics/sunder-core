namespace Sunder.Host.Supervisor;

internal sealed record HostStartupOptions(
    string? RuntimeHostPath,
    TimeSpan WorkerStartupTimeout,
    string? HostStateRoot,
    string? RuntimeStateRoot)
{
    internal const string RuntimeHostPathEnvironmentVariable = "SUNDER_HOST_RUNTIME_PATH";
    internal const string HostStateRootEnvironmentVariable = "SUNDER_HOST_STATE_ROOT";
    internal const string RuntimeStateRootEnvironmentVariable = "SUNDER_RUNTIME_STATE_ROOT";

    public static HostStartupOptions Parse(
        IReadOnlyList<string> args,
        Func<string, string?>? getEnvironmentVariable = null,
        string? baseDirectory = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        string? configuredRuntimePath = getEnvironmentVariable(RuntimeHostPathEnvironmentVariable);
        string? hostStateRoot = getEnvironmentVariable(HostStateRootEnvironmentVariable);
        string? runtimeStateRoot = getEnvironmentVariable(RuntimeStateRootEnvironmentVariable);
        var startupTimeout = TimeSpan.FromSeconds(30);
        for (var index = 0; index < args.Count; index++)
        {
            if (TryReadValue(args, ref index, "--runtime-host-path", out var value))
            {
                configuredRuntimePath = value;
                continue;
            }

            if (TryReadValue(args, ref index, "--host-state-root", out value))
            {
                hostStateRoot = value;
                continue;
            }

            if (TryReadValue(args, ref index, "--runtime-state-root", out value))
            {
                runtimeStateRoot = value;
                continue;
            }

            if (TryReadValue(args, ref index, "--worker-startup-timeout-seconds", out value))
            {
                if (!double.TryParse(
                        value,
                        System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var seconds)
                    || seconds <= 0)
                {
                    throw new ArgumentException(
                        "--worker-startup-timeout-seconds requires a positive number.",
                        nameof(args));
                }
                startupTimeout = TimeSpan.FromSeconds(seconds);
            }
        }

        hostStateRoot = NormalizeOptionalPath(hostStateRoot);
        runtimeStateRoot = NormalizeOptionalPath(runtimeStateRoot);

        var runtimeHostPath = ResolveRuntimeHostPath(configuredRuntimePath, baseDirectory);
        return new HostStartupOptions(
            runtimeHostPath,
            startupTimeout,
            hostStateRoot,
            runtimeStateRoot);
    }

    internal static string? ResolveRuntimeHostPath(string? configuredPath, string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        return ResolveFromPath(configuredPath)
               ?? ResolveFromPath(Path.Combine(baseDirectory, "RuntimeHost"))
               ?? ResolveFromPath(baseDirectory);
    }

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string option,
        out string? value)
    {
        if (!string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
        {
            value = null;
            return false;
        }
        if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{option} requires a value.", nameof(args));
        }
        value = args[index];
        return true;
    }

    private static string? NormalizeOptionalPath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

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
