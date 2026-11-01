namespace Sunder.Runtime.Host.Services;

internal static class ExecutionRuntimeGuardianEnvironment
{
    internal static void Configure(IDictionary<string, string?> environment, string runtimeDirectory)
    {
        Clear(environment);

        // The guardian is Host code, not a package. Bootstrap it with the runtime
        // already executing the Host, rather than ambient or machine-wide lookup.
        var runtime = new DirectoryInfo(Path.TrimEndingDirectorySeparator(runtimeDirectory));
        if (runtime.Parent is { Name: "Microsoft.NETCore.App", Parent: { Name: "shared", Parent: { } root } })
        {
            environment["DOTNET_ROOT"] = root.FullName;
        }
        // Self-contained deployments locate their app-local runtime without a root.
    }

    internal static void Clear(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys
                     .Where(name => name.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            environment.Remove(name);
        }
    }
}
