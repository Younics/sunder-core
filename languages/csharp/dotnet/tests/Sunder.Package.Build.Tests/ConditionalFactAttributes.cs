using Xunit;

namespace Sunder.Package.Build.Tests;

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
