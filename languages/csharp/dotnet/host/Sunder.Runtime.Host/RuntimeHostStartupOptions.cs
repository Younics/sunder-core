namespace Sunder.Runtime.Host;

public sealed class RuntimeHostStartupOptions
{
    private const string PreparationConcurrencyEnvironmentVariable = "SUNDER_PACKAGE_PREPARATION_CONCURRENCY";
    private const string WorkerStartupConcurrencyEnvironmentVariable = "SUNDER_PACKAGE_WORKER_STARTUP_CONCURRENCY";

    public bool WaitForDebugger { get; init; }

    public bool DevelopmentAllowNonLoopbackRuntimeListen { get; init; }

    public int PackagePreparationConcurrency { get; init; } =
        Services.PackageSessionConcurrencyOptions.DefaultMaximumConcurrency;

    public int PackageWorkerStartupConcurrency { get; init; } =
        Services.PackageSessionConcurrencyOptions.DefaultMaximumConcurrency;

    public static RuntimeHostStartupOptions Parse(IReadOnlyList<string> args)
    {
        var waitForDebugger = false;
        var developmentAllowNonLoopbackRuntimeListen = false;
        int? packagePreparationConcurrency = null;
        int? packageWorkerStartupConcurrency = null;
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

            if (string.Equals(argument, "--package-preparation-concurrency", StringComparison.OrdinalIgnoreCase))
            {
                packagePreparationConcurrency = ParseConcurrencyArgument(args, ref index, argument);
                continue;
            }

            if (string.Equals(argument, "--package-worker-startup-concurrency", StringComparison.OrdinalIgnoreCase))
            {
                packageWorkerStartupConcurrency = ParseConcurrencyArgument(args, ref index, argument);
                continue;
            }

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

        packagePreparationConcurrency ??= ParseConcurrencyEnvironment(PreparationConcurrencyEnvironmentVariable);
        packageWorkerStartupConcurrency ??= ParseConcurrencyEnvironment(WorkerStartupConcurrencyEnvironmentVariable);

        return new RuntimeHostStartupOptions
        {
            WaitForDebugger = waitForDebugger,
            DevelopmentAllowNonLoopbackRuntimeListen = developmentAllowNonLoopbackRuntimeListen,
            PackagePreparationConcurrency = packagePreparationConcurrency
                ?? Services.PackageSessionConcurrencyOptions.DefaultMaximumConcurrency,
            PackageWorkerStartupConcurrency = packageWorkerStartupConcurrency
                ?? Services.PackageSessionConcurrencyOptions.DefaultMaximumConcurrency,
        };
    }

    private static int ParseConcurrencyArgument(
        IReadOnlyList<string> args,
        ref int index,
        string argument)
    {
        if (++index >= args.Count)
        {
            throw new ArgumentException($"Argument '{argument}' requires a value.");
        }
        return ParseConcurrency(args[index], argument);
    }

    private static int? ParseConcurrencyEnvironment(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? null : ParseConcurrency(value, variable);
    }

    private static int ParseConcurrency(string value, string source)
    {
        if (!int.TryParse(value, out var parsed)
            || parsed is < Services.PackageSessionConcurrencyOptions.MinimumConcurrency
                or > Services.PackageSessionConcurrencyOptions.MaximumConcurrency)
        {
            throw new ArgumentException(
                $"{source} must be an integer between {Services.PackageSessionConcurrencyOptions.MinimumConcurrency} and {Services.PackageSessionConcurrencyOptions.MaximumConcurrency}.");
        }
        return parsed;
    }
}
