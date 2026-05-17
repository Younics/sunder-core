using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sunder.App.Views.Controls;

public partial class PackageListRow : UserControl
{
    public static readonly StyledProperty<IImage?> IconImageProperty =
        AvaloniaProperty.Register<PackageListRow, IImage?>(nameof(IconImage));

    public static readonly StyledProperty<bool> HasIconImageProperty =
        AvaloniaProperty.Register<PackageListRow, bool>(nameof(HasIconImage));

    public static readonly StyledProperty<string> GlyphProperty =
        AvaloniaProperty.Register<PackageListRow, string>(nameof(Glyph), "?");

    public static readonly StyledProperty<bool> ShowGlyphFallbackProperty =
        AvaloniaProperty.Register<PackageListRow, bool>(nameof(ShowGlyphFallback));

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PackageListRow, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> PackageIdProperty =
        AvaloniaProperty.Register<PackageListRow, string>(nameof(PackageId), string.Empty);

    public static readonly StyledProperty<string> PrimaryBadgeProperty =
        AvaloniaProperty.Register<PackageListRow, string>(nameof(PrimaryBadge), string.Empty);

    public static readonly StyledProperty<string> DetailBadgeProperty =
        AvaloniaProperty.Register<PackageListRow, string>(nameof(DetailBadge), string.Empty);

    public static readonly StyledProperty<bool> ShowDetailBadgeProperty =
        AvaloniaProperty.Register<PackageListRow, bool>(nameof(ShowDetailBadge), true);

    public static readonly StyledProperty<string> ActionBadgeProperty =
        AvaloniaProperty.Register<PackageListRow, string>(nameof(ActionBadge), string.Empty);

    public static readonly StyledProperty<bool> ShowBadgeRowProperty =
        AvaloniaProperty.Register<PackageListRow, bool>(nameof(ShowBadgeRow), true);

    public static readonly StyledProperty<bool> ShowActionBadgeProperty =
        AvaloniaProperty.Register<PackageListRow, bool>(nameof(ShowActionBadge), true);

    public static readonly StyledProperty<bool> ActionBadgeIsUpdateProperty =
        AvaloniaProperty.Register<PackageListRow, bool>(nameof(ActionBadgeIsUpdate));

    public static readonly StyledProperty<string> OperationStatusTextProperty =
        AvaloniaProperty.Register<PackageListRow, string>(nameof(OperationStatusText), string.Empty);

    public static readonly StyledProperty<bool> ShowOperationStatusProperty =
        AvaloniaProperty.Register<PackageListRow, bool>(nameof(ShowOperationStatus));

    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<PackageListRow, bool>(nameof(IsSelected));

    public PackageListRow()
    {
        InitializeComponent();
    }

    public IImage? IconImage
    {
        get => GetValue(IconImageProperty);
        set => SetValue(IconImageProperty, value);
    }

    public bool HasIconImage
    {
        get => GetValue(HasIconImageProperty);
        set => SetValue(HasIconImageProperty, value);
    }

    public string Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public bool ShowGlyphFallback
    {
        get => GetValue(ShowGlyphFallbackProperty);
        set => SetValue(ShowGlyphFallbackProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string PackageId
    {
        get => GetValue(PackageIdProperty);
        set => SetValue(PackageIdProperty, value);
    }

    public string PrimaryBadge
    {
        get => GetValue(PrimaryBadgeProperty);
        set => SetValue(PrimaryBadgeProperty, value);
    }

    public string DetailBadge
    {
        get => GetValue(DetailBadgeProperty);
        set => SetValue(DetailBadgeProperty, value);
    }

    public bool ShowDetailBadge
    {
        get => GetValue(ShowDetailBadgeProperty);
        set => SetValue(ShowDetailBadgeProperty, value);
    }

    public string ActionBadge
    {
        get => GetValue(ActionBadgeProperty);
        set => SetValue(ActionBadgeProperty, value);
    }

    public bool ShowBadgeRow
    {
        get => GetValue(ShowBadgeRowProperty);
        set => SetValue(ShowBadgeRowProperty, value);
    }

    public bool ShowActionBadge
    {
        get => GetValue(ShowActionBadgeProperty);
        set => SetValue(ShowActionBadgeProperty, value);
    }

    public bool ActionBadgeIsUpdate
    {
        get => GetValue(ActionBadgeIsUpdateProperty);
        set => SetValue(ActionBadgeIsUpdateProperty, value);
    }

    public string OperationStatusText
    {
        get => GetValue(OperationStatusTextProperty);
        set => SetValue(OperationStatusTextProperty, value);
    }

    public bool ShowOperationStatus
    {
        get => GetValue(ShowOperationStatusProperty);
        set => SetValue(ShowOperationStatusProperty, value);
    }

    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }
}
