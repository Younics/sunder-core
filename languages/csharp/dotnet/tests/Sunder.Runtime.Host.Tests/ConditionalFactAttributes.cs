using System.Diagnostics;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Requires a Unix platform.";
    }
}

internal sealed class ExternalArtifactFactAttribute : FactAttribute
{
    public ExternalArtifactFactAttribute(string requirementVariable, params string[] inputVariables)
    {
        var selected = string.Equals(
            Environment.GetEnvironmentVariable(requirementVariable),
            "1",
            StringComparison.Ordinal);
        if (!selected)
        {
            Skip = $"External artifact lane is not selected. Set {requirementVariable}=1 to require it.";
            return;
        }
        foreach (var variable in inputVariables)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
            {
                throw new InvalidOperationException($"Selected external artifact lane requires {variable}.");
            }
        }
    }
}

internal sealed class Node24FactAttribute : FactAttribute
{
    private const string RequiredVariable = "SUNDER_REQUIRE_NODE24_HOST_INTEGRATION";

    public Node24FactAttribute()
    {
        var availability = Node24Availability.Current;
        if (availability.IsAvailable)
        {
            return;
        }
        if (string.Equals(Environment.GetEnvironmentVariable(RequiredVariable), "1", StringComparison.Ordinal))
        {
            return;
        }
        Skip = availability.UnavailableReason;
    }
}

internal static class Node24Availability
{
    private static readonly Lazy<Node24Probe> Probe = new(Detect);

    public static Node24Probe Current => Probe.Value;

    private static Node24Probe Detect()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "node",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add("process.versions.node + '\\n' + process.execPath");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return Node24Probe.Unavailable("Requires local Node 24; 'node' could not be started.");
            }
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return Node24Probe.Unavailable("Requires local Node 24; 'node' did not report its version within five seconds.");
            }
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd().Trim();
            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (process.ExitCode != 0 || lines.Length != 2)
            {
                var detail = error.Length == 0 ? $"exit code {process.ExitCode}" : error;
                return Node24Probe.Unavailable($"Requires local Node 24; 'node' version probe failed ({detail}).");
            }
            var version = lines[0].TrimStart('v');
            if (!int.TryParse(version.Split('.')[0], out var major) || major != 24)
            {
                return Node24Probe.Unavailable(
                    $"Requires local Node 24; 'node' resolved to v{version} at '{lines[1]}'.");
            }
            return new Node24Probe(true, Path.GetFullPath(lines[1]), version, string.Empty);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or System.ComponentModel.Win32Exception
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            return Node24Probe.Unavailable(
                $"Requires local Node 24; 'node' is unavailable ({exception.Message}).");
        }
    }
}

internal sealed record Node24Probe(
    bool IsAvailable,
    string? ExecutablePath,
    string? Version,
    string UnavailableReason)
{
    public static Node24Probe Unavailable(string reason) => new(false, null, null, reason);
}
