using Xunit;

namespace Sunder.Package.Build.Tests;

internal sealed class MacOsFactAttribute : FactAttribute
{
    public MacOsFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Requires macOS.";
    }
}

internal sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Requires a Unix platform.";
    }
}

internal sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Requires Linux process identity metadata.";
    }
}

internal sealed class ExternalArtifactFactAttribute : FactAttribute
{
    public ExternalArtifactFactAttribute(string requirementVariable)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(requirementVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"External interop lane is not selected. Set {requirementVariable}=1 to require it.";
        }
    }
}
