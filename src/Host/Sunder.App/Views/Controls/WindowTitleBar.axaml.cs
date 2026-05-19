using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Sunder.App.Views;

namespace Sunder.App.Views.Controls;

public partial class WindowTitleBar : UserControl
{
    public static readonly StyledProperty<object?> LeadingContentProperty =
        AvaloniaProperty.Register<WindowTitleBar, object?>(nameof(LeadingContent));

    public static readonly StyledProperty<object?> CenterContentProperty =
        AvaloniaProperty.Register<WindowTitleBar, object?>(nameof(CenterContent));

    public static readonly StyledProperty<object?> TrailingContentProperty =
        AvaloniaProperty.Register<WindowTitleBar, object?>(nameof(TrailingContent));

    public static readonly StyledProperty<bool> IsLogoLargeProperty =
        AvaloniaProperty.Register<WindowTitleBar, bool>(nameof(IsLogoLarge));

    public WindowTitleBar()
    {
        InitializeComponent();
        var isMac = OperatingSystem.IsMacOS();
        LeftChromeControls.IsVisible = isMac;
        RightChromeControls.IsVisible = !isMac;
        LeftLogo.IsVisible = !isMac;
        RightLogo.IsVisible = isMac;
        LeftChromeSeparator.IsVisible = isMac;
    }

    public object? LeadingContent
    {
        get => GetValue(LeadingContentProperty);
        set => SetValue(LeadingContentProperty, value);
    }

    public object? CenterContent
    {
        get => GetValue(CenterContentProperty);
        set => SetValue(CenterContentProperty, value);
    }

    public object? TrailingContent
    {
        get => GetValue(TrailingContentProperty);
        set => SetValue(TrailingContentProperty, value);
    }

    public bool IsLogoLarge
    {
        get => GetValue(IsLogoLargeProperty);
        set => SetValue(IsLogoLargeProperty, value);
    }

    private void ToolbarDragHost_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            WindowDragHost.BeginWindowDragOrToggleMaximize(window, e);
        }
    }
}
