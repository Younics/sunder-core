using Xunit;

namespace Sunder.App.Integration.Tests;

internal sealed class ExternalArtifactFactAttribute : FactAttribute
{
    public ExternalArtifactFactAttribute(string requirementVariable, params string[] inputVariables)
    {
        if (Environment.GetEnvironmentVariable(requirementVariable) != "1")
        {
            Skip = $"External artifact lane is not selected. Set {requirementVariable}=1 to require it.";
            return;
        }
        foreach (var variable in inputVariables)
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                throw new InvalidOperationException($"Selected external artifact lane requires {variable}.");
    }
}

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows.";
    }
}

internal sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Requires Linux.";
    }
}

internal sealed class MacOsFactAttribute : FactAttribute
{
    public MacOsFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Requires macOS.";
    }
}
