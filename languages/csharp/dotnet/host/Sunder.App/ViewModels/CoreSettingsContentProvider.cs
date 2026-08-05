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
                "Runtime settings will control shell behavior, loading preferences, and runtime diagnostics.",
                "Package-specific configuration is rendered below the Packages separator when a package contributes a config schema.",
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
                "Advanced settings will eventually host diagnostic and power-user controls for package developers and operators.",
            ],
            _ =>
            [
                "Select a core section or a package entry to configure it.",
            ],
        };
}
