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
