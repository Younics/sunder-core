using Sunder.App.Themes;

namespace Sunder.App.Models;

public sealed class ShellState
{
    public const int CurrentLayoutVersion = 2;
    public const double DefaultLeftPanelWidth = 360;
    public const double DefaultRightPanelWidth = 360;
    public const double DefaultTopRowHeightRatio = 0.80;
    public const double DefaultBottomSplitRatio = 0.5;
    public const double DefaultSettingsSidebarWidth = 250;
    public const double DefaultPackagesSidebarWidth = 430;

    public int LayoutVersion { get; set; } = CurrentLayoutVersion;

    public bool HasInitializedLayout { get; set; }

    public Dictionary<string, RailPlacement> ViewPlacements { get; set; } = [];

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

    public ShellWindowPlacement? SettingsWindowPlacement { get; set; }

    public ShellWindowPlacement? PackagesWindowPlacement { get; set; }

    public string ThemeId { get; set; } = SunderThemeDefinition.DefaultDark.Id;

    public string? PreferredRuntimeUrl { get; set; }
}

public sealed class ShellWindowPlacement
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool IsMaximized { get; set; }
}
