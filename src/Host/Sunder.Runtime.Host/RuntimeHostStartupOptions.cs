namespace Sunder.Runtime.Host;

public sealed class RuntimeHostStartupOptions
{
    public bool WaitForDebugger { get; init; }

    public IReadOnlyList<string> DevPackageFolders { get; init; } = [];

    public bool DevelopmentAllowNonLoopbackRuntimeListen { get; init; }

    public static RuntimeHostStartupOptions Parse(IReadOnlyList<string> args)
    {
        var waitForDebugger = false;
        var developmentAllowNonLoopbackRuntimeListen = false;
        var devPackageFolders = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--wait-for-debugger", StringComparison.OrdinalIgnoreCase))
            {
                waitForDebugger = true;
                continue;
            }

            if (string.Equals(argument, "--development-allow-non-loopback-runtime-listen", StringComparison.OrdinalIgnoreCase))
            {
                developmentAllowNonLoopbackRuntimeListen = true;
                continue;
            }

            if (!TryReadOptionValue(args, ref index, argument, "--dev-package", out var devPackageFolder)
                || string.IsNullOrWhiteSpace(devPackageFolder))
            {
                continue;
            }

            devPackageFolders.Add(Path.GetFullPath(devPackageFolder));
        }

        if (!waitForDebugger)
        {
            var environmentValue = Environment.GetEnvironmentVariable("SUNDER_WAIT_FOR_DEBUGGER");
            waitForDebugger = string.Equals(environmentValue, "1", StringComparison.OrdinalIgnoreCase)
                || bool.TryParse(environmentValue, out var enabled) && enabled;
        }

        if (!developmentAllowNonLoopbackRuntimeListen)
        {
            var environmentValue = Environment.GetEnvironmentVariable("SUNDER_DEVELOPMENT_ALLOW_NON_LOOPBACK_RUNTIME_LISTEN");
            developmentAllowNonLoopbackRuntimeListen = string.Equals(environmentValue, "1", StringComparison.OrdinalIgnoreCase)
                || bool.TryParse(environmentValue, out var enabled) && enabled;
        }

        return new RuntimeHostStartupOptions
        {
            WaitForDebugger = waitForDebugger,
            DevelopmentAllowNonLoopbackRuntimeListen = developmentAllowNonLoopbackRuntimeListen,
            DevPackageFolders = devPackageFolders
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
    }

    private static bool TryReadOptionValue(
        IReadOnlyList<string> args,
        ref int index,
        string argument,
        string optionName,
        out string? value)
    {
        if (string.Equals(argument, optionName, StringComparison.OrdinalIgnoreCase))
        {
            value = index + 1 < args.Count ? args[++index] : null;
            return true;
        }

        var prefix = optionName + "=";
        if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = argument[prefix.Length..];
            return true;
        }

        value = null;
        return false;
    }
}
