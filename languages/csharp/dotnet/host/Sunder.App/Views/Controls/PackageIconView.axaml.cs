using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sunder.App.Views.Controls;

public partial class PackageIconView : UserControl
{
    public static readonly StyledProperty<IImage?> IconImageProperty =
        AvaloniaProperty.Register<PackageIconView, IImage?>(nameof(IconImage));

    public static readonly StyledProperty<string> GlyphProperty =
        AvaloniaProperty.Register<PackageIconView, string>(nameof(Glyph), "?");

    public static readonly StyledProperty<bool> HasIconImageProperty =
        AvaloniaProperty.Register<PackageIconView, bool>(nameof(HasIconImage));

    public static readonly StyledProperty<bool> ShowFallbackContainerProperty =
        AvaloniaProperty.Register<PackageIconView, bool>(nameof(ShowFallbackContainer));

    public static readonly StyledProperty<bool> ShowBareGlyphFallbackProperty =
        AvaloniaProperty.Register<PackageIconView, bool>(nameof(ShowBareGlyphFallback));

    public static readonly StyledProperty<Thickness> ImageMarginProperty =
        AvaloniaProperty.Register<PackageIconView, Thickness>(nameof(ImageMargin));

    public static readonly StyledProperty<CornerRadius> FallbackCornerRadiusProperty =
        AvaloniaProperty.Register<PackageIconView, CornerRadius>(nameof(FallbackCornerRadius), new CornerRadius(8));

    public static readonly StyledProperty<Thickness> FallbackPaddingProperty =
        AvaloniaProperty.Register<PackageIconView, Thickness>(nameof(FallbackPadding), new Thickness(3));

    public static readonly StyledProperty<double> GlyphFontSizeProperty =
        AvaloniaProperty.Register<PackageIconView, double>(nameof(GlyphFontSize), 18);

    public static readonly StyledProperty<FontWeight> GlyphFontWeightProperty =
        AvaloniaProperty.Register<PackageIconView, FontWeight>(nameof(GlyphFontWeight), FontWeight.SemiBold);

    public static readonly StyledProperty<IBrush?> GlyphForegroundProperty =
        AvaloniaProperty.Register<PackageIconView, IBrush?>(nameof(GlyphForeground));

    public PackageIconView()
    {
        InitializeComponent();
    }

    public IImage? IconImage
    {
        get => GetValue(IconImageProperty);
        set => SetValue(IconImageProperty, value);
    }

    public string Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public bool HasIconImage
    {
        get => GetValue(HasIconImageProperty);
        set => SetValue(HasIconImageProperty, value);
    }

    public bool ShowFallbackContainer
    {
        get => GetValue(ShowFallbackContainerProperty);
        set => SetValue(ShowFallbackContainerProperty, value);
    }

    public bool ShowBareGlyphFallback
    {
        get => GetValue(ShowBareGlyphFallbackProperty);
        set => SetValue(ShowBareGlyphFallbackProperty, value);
    }

    public Thickness ImageMargin
    {
        get => GetValue(ImageMarginProperty);
        set => SetValue(ImageMarginProperty, value);
    }

    public CornerRadius FallbackCornerRadius
    {
        get => GetValue(FallbackCornerRadiusProperty);
        set => SetValue(FallbackCornerRadiusProperty, value);
    }

    public Thickness FallbackPadding
    {
        get => GetValue(FallbackPaddingProperty);
        set => SetValue(FallbackPaddingProperty, value);
    }

    public double GlyphFontSize
    {
        get => GetValue(GlyphFontSizeProperty);
        set => SetValue(GlyphFontSizeProperty, value);
    }

    public FontWeight GlyphFontWeight
    {
        get => GetValue(GlyphFontWeightProperty);
        set => SetValue(GlyphFontWeightProperty, value);
    }

    public IBrush? GlyphForeground
    {
        get => GetValue(GlyphForegroundProperty);
        set => SetValue(GlyphForegroundProperty, value);
    }
}
