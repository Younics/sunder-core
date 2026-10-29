using Sunder.App.Themes;
using System.Text.Json.Serialization;

namespace Sunder.App.Models;

public sealed class ShellState
{
    public long Revision { get; set; }

    public const int CurrentLayoutVersion = 2;
    public const double DefaultLeftPanelWidth = 360;
    public const double DefaultRightPanelWidth = 360;
    public const double DefaultTopRowHeightRatio = 0.80;
    public const double DefaultBottomSplitRatio = 0.5;
    public const double DefaultSettingsSidebarWidth = 250;
    public const double DefaultPackagesSidebarWidth = 430;
    public const double DefaultStacksSidebarWidth = 390;
    public const double DefaultBackgroundTaskPopoverWidth = 500;
    public const double DefaultBackgroundTaskPopoverHeight = 360;
    public const double MinimumBackgroundTaskPopoverWidth = 360;
    public const double MaximumBackgroundTaskPopoverWidth = 760;
    public const double MinimumBackgroundTaskPopoverHeight = 220;
    public const double MaximumBackgroundTaskPopoverHeight = 640;

    public int LayoutVersion { get; set; } = CurrentLayoutVersion;

    public bool HasInitializedLayout { get; set; }

    public Dictionary<string, RailPlacement> ViewPlacements { get; set; } = [];

    // App view ids are opaque; Runtime's package catalog does not enumerate all
    // App registrations. Retain ownership to distinguish absence from removal.
    public Dictionary<string, string> ViewPackageIds { get; set; } = [];

    public Dictionary<string, int> ViewOrder { get; set; } = [];

    public HashSet<string> HiddenHotbarViewIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? SelectedLeftTopViewId { get; set; }

    public string? SelectedMiddleViewId { get; set; }

    public string? SelectedRightTopViewId { get; set; }

    public string? SelectedLeftBottomViewId { get; set; }

    public string? SelectedRightBottomViewId { get; set; }

    public double LeftPanelWidth { get; set; } = DefaultLeftPanelWidth;

    public double RightPanelWidth { get; set; } = DefaultRightPanelWidth;

    public double TopRowHeightRatio { get; set; } = DefaultTopRowHeightRatio;

    public double BottomSplitRatio { get; set; } = DefaultBottomSplitRatio;

    public double SettingsSidebarWidth { get; set; } = DefaultSettingsSidebarWidth;

    public double PackagesSidebarWidth { get; set; } = DefaultPackagesSidebarWidth;

    public double StacksSidebarWidth { get; set; } = DefaultStacksSidebarWidth;

    // Source terminology may change; persisted user-setting keys must not.
    [JsonPropertyName("BackgroundProcessPopoverWidth")]
    public double BackgroundTaskPopoverWidth { get; set; } = DefaultBackgroundTaskPopoverWidth;

    [JsonPropertyName("BackgroundProcessPopoverHeight")]
    public double BackgroundTaskPopoverHeight { get; set; } = DefaultBackgroundTaskPopoverHeight;

    public ShellWindowPlacement? MainWindowPlacement { get; set; }

    public ShellWindowPlacement? SettingsWindowPlacement { get; set; }

    public ShellWindowPlacement? PackagesWindowPlacement { get; set; }

    public ShellWindowPlacement? StacksWindowPlacement { get; set; }

    public ShellWindowPlacement? CreateStackWizardWindowPlacement { get; set; }

    public ShellWindowPlacement? UseStackWizardWindowPlacement { get; set; }

    public string ThemeId { get; set; } = SunderThemeDefinition.GraphiteDark.Id;

    public string? PreferredRuntimeUrl { get; set; }
}

public sealed class ShellWindowPlacement
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool IsMaximized { get; set; }

    public bool IsFullScreen { get; set; }
}
