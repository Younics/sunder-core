namespace Sunder.App.ViewModels;

internal static class CoreSettingsContentProvider
{
    public static IReadOnlyList<string> GetLines(string sectionId)
        => sectionId switch
        {
            "runtime" =>
            [
                "Package Runtimes manages the .NET and Node.js runtimes used to run packages.",
            ],
            "advanced" =>
            [
                "Advanced settings control App-owned Runtime launch policy and other power-user behavior.",
            ],
            _ =>
            [
                "Select a core section or a package entry to configure it.",
            ],
        };
}
