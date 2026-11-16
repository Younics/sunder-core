using Xunit;

namespace Sunder.ExecutionRuntime.Build.Tests;

internal sealed class WindowsOrMacOsFactAttribute : FactAttribute
{
    public WindowsOrMacOsFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) Skip = "Requires Windows or macOS.";
    }
}

internal sealed class WindowsOrMacOsTheoryAttribute : TheoryAttribute
{
    public WindowsOrMacOsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) Skip = "Requires Windows or macOS.";
    }
}
