using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sunder.App.Views.Controls;

// Native host rendering for schema-based package settings; no dependency on an Agent package.
internal sealed class SettingsFieldHeader : Grid
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<SettingsFieldHeader, string?>(nameof(Title));
    public static readonly StyledProperty<string?> HelpTextProperty = AvaloniaProperty.Register<SettingsFieldHeader, string?>(nameof(HelpText));
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? HelpText { get => GetValue(HelpTextProperty); set => SetValue(HelpTextProperty, value); }
    private readonly TextBlock _title;
    private readonly SettingsHelpButton _help;

    public SettingsFieldHeader()
    {
        ColumnDefinitions = new("Auto,Auto");
        Bind(ColumnSpacingProperty, this.GetResourceObservable("Sunder.Spacing.XSmall"));
        _title = new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _title.Bind(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = this });
        _help = new SettingsHelpButton();
        _help.Bind(SettingsHelpButton.HelpTextProperty, new Binding(nameof(HelpText)) { Source = this });
        SetColumn(_help, 1);
        Children.Add(_title);
        Children.Add(_help);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _title.MaxWidth = double.IsFinite(availableSize.Width)
            ? Math.Max(0, availableSize.Width - (_help.IsVisible ? 28 + ColumnSpacing : 0)) : double.PositiveInfinity;
        return base.MeasureOverride(availableSize);
    }
}

internal sealed class SettingsHelpButton : Button
{
    public static readonly StyledProperty<string?> HelpTextProperty = AvaloniaProperty.Register<SettingsHelpButton, string?>(nameof(HelpText));
    public string? HelpText { get => GetValue(HelpTextProperty); set => SetValue(HelpTextProperty, value); }
    protected override Type StyleKeyOverride => typeof(Button);

    public SettingsHelpButton()
    {
        IsVisible = false;
        Classes.Add("settings-info");
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(this, "More information");
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 14, Height = 14, Stretch = Stretch.Uniform,
            Data = Geometry.Parse("M7,0.75 A6.25,6.25 0 1 1 6.99,0.75 M7,6.25 L7,10 M7,3.75 L7,4"),
            StrokeThickness = 1.25, StrokeLineCap = PenLineCap.Round,
        };
        icon.Bind(Shape.StrokeProperty, new Binding(nameof(Foreground)) { Source = this });
        Content = icon;
        var text = new SelectableTextBlock { MaxWidth = 360, TextWrapping = TextWrapping.Wrap };
        text.Bind(SelectableTextBlock.TextProperty, new Binding(nameof(HelpText)) { Source = this });
        text.Bind(FontSizeProperty, this.GetResourceObservable("Sunder.FontSize.Body"));
        text.Bind(ForegroundProperty, this.GetResourceObservable("Sunder.Brush.Foreground.Primary"));
        Flyout = new Flyout { Content = text, FlyoutPresenterClasses = { "settings-help-popover" } };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != HelpTextProperty) return;
        SetCurrentValue(IsVisibleProperty, !string.IsNullOrWhiteSpace(HelpText));
        AutomationProperties.SetHelpText(this, HelpText);
    }
}
