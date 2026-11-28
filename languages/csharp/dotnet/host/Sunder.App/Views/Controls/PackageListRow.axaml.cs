using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sunder.App.ViewModels;

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

    public static readonly StyledProperty<string> StatusProperty =
        AvaloniaProperty.Register<PackageListRow, string>(nameof(Status), string.Empty);

    public static readonly StyledProperty<string> ActionBadgeProperty =
        AvaloniaProperty.Register<PackageListRow, string>(nameof(ActionBadge), string.Empty);

    public static readonly StyledProperty<bool> ShowActionBadgeProperty =
        AvaloniaProperty.Register<PackageListRow, bool>(nameof(ShowActionBadge), true);

    public static readonly StyledProperty<string> OperationStatusTextProperty =
        AvaloniaProperty.Register<PackageListRow, string>(nameof(OperationStatusText), string.Empty);

    public static readonly StyledProperty<bool> ShowOperationStatusProperty =
        AvaloniaProperty.Register<PackageListRow, bool>(nameof(ShowOperationStatus));

    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<PackageListRow, bool>(nameof(IsSelected));

    public PackageListRow()
    {
        InitializeComponent();
        EffectiveViewportChanged += (_, args) =>
        {
            _inViewport = args.EffectiveViewport.Intersects(new Rect(Bounds.Size));
            LoadRowIcon();
        };
        DataContextChanged += (_, _) => LoadRowIcon();
    }

    private bool _inViewport;

    private void LoadRowIcon()
    {
        if (_inViewport && IsEffectivelyVisible && DataContext is PackageIconItemViewModel row) row.EnsureIconLoaded();
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

    public string Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public string ActionBadge
    {
        get => GetValue(ActionBadgeProperty);
        set => SetValue(ActionBadgeProperty, value);
    }

    public bool ShowActionBadge
    {
        get => GetValue(ShowActionBadgeProperty);
        set => SetValue(ShowActionBadgeProperty, value);
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
