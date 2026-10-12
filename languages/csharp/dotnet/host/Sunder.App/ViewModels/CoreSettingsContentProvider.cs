namespace Sunder.App.ViewModels;

internal static class CoreSettingsContentProvider
{
    public static IReadOnlyList<string> GetLines(string sectionId)
        => sectionId switch
        {
            "appearance" =>
            [
                "Appearance keeps host-level theme and presentation settings.",
                "Package-owned UI should still consume Sunder semantic theme tokens instead of mutating shell chrome.",
            ],
            "runtime" =>
            [
                "Package Runtimes manages the .NET and Node.js runtimes used to run packages.",
            ],
            "updates" =>
            [
                "Package updates and managed rollout workflows are planned but not implemented in this slice.",
            ],
            "notifications" =>
            [
                "Notification preferences will later control package alerts, runtime warnings, and task completion signals.",
            ],
            "privacy" =>
            [
                "Sunder remains local-first. Package-specific secrets and trust decisions will continue to evolve here.",
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
