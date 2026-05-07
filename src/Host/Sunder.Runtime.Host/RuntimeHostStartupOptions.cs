namespace Sunder.Runtime.Host;

public sealed class RuntimeHostStartupOptions
{
    public bool WaitForDebugger { get; init; }

    public static RuntimeHostStartupOptions Parse(IReadOnlyList<string> args)
    {
        var waitForDebugger = args.Any(x =>
            string.Equals(x, "--wait-for-debugger", StringComparison.OrdinalIgnoreCase)
        );

        if (!waitForDebugger)
        {
            var environmentValue = Environment.GetEnvironmentVariable("SUNDER_WAIT_FOR_DEBUGGER");
            waitForDebugger = string.Equals(environmentValue, "1", StringComparison.OrdinalIgnoreCase)
                || bool.TryParse(environmentValue, out var enabled) && enabled;
        }

        return new RuntimeHostStartupOptions { WaitForDebugger = waitForDebugger };
    }
}
