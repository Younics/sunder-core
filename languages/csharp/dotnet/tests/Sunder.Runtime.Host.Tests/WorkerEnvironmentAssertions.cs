using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

internal static class WorkerEnvironmentAssertions
{
    internal static string[] ExpectedLaunchNames()
    {
        var names = new List<string>
        {
            "SUNDER_ACTIVATION_ID",
            "SUNDER_DEVELOPMENT_TOOLCHAIN",
            "SUNDER_PACKAGE_CONTENT_ROOT",
            "SUNDER_PACKAGE_ID",
            "SUNDER_PACKAGE_VERSION",
            "SUNDER_SESSION_ID",
            "SUNDER_WORKER_OPERATIONAL_OUTCOMES_V1",
            "SUNDER_WORKER_PROTOCOL",
            "SUNDER_WORKER_TEMP_ROOT",
            "TEMP",
            "TMP",
            "TMPDIR",
        };
        if (OperatingSystem.IsWindows()) names.AddRange(["SystemRoot", "WINDIR"]);
        else if (OperatingSystem.IsMacOS()) names.Add("__CF_USER_TEXT_ENCODING");
        return names.Select(NormalizeName).Order(StringComparer.Ordinal).ToArray();
    }

    internal static string NormalizeName(string name)
        => OperatingSystem.IsWindows() ? name.ToUpperInvariant() : name;

    internal static void AssertObservedChild(JsonElement report)
        => AssertObservedChild(report, ExpectedLaunchNames(), OperatingSystem.IsWindows(),
            RuntimeInformation.OSArchitecture, RuntimeInformation.ProcessArchitecture);

    internal static void AssertObservedChild(JsonElement report, string[] launchNames,
        bool windows, Architecture osArchitecture, Architecture workerArchitecture)
    {
        var names = report.GetProperty("environmentNames").EnumerateArray()
            .Select(item => windows ? item.GetString()!.ToUpperInvariant() : item.GetString()!).ToList();
        if (names.Remove("PROCESSOR_ARCHITECTURE"))
        {
            // Windows ARM64 startup can synthesize this even when the explicit
            // launch block omits it. It is not an inherited launch permission:
            // allow only this key, on this OS, with the selected native worker's value.
            Assert.True(windows && osArchitecture == Architecture.Arm64);
            Assert.True(workerArchitecture is Architecture.Arm64 or Architecture.X64);
            Assert.Equal(workerArchitecture == Architecture.Arm64 ? "ARM64" : "AMD64",
                report.GetProperty("processorArchitecture").GetString());
        }
        else Assert.Equal(JsonValueKind.Null, report.GetProperty("processorArchitecture").ValueKind);
        Assert.Equal(launchNames, names.Order(StringComparer.Ordinal));
    }
}
