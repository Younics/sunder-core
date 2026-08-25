using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Sunder.App.ViewModels;

namespace Sunder.App.Views.Controls;

public partial class MarketplaceMediaStrip : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<MarketplaceMediaStrip, IEnumerable?>(nameof(Items));

    public MarketplaceMediaStrip()
    {
        InitializeComponent();
    }

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    private void MediaFrame_OnEffectiveViewportChanged(
        object? sender,
        EffectiveViewportChangedEventArgs e)
    {
        if (sender is not Control control
            || e.EffectiveViewport.Width <= 0
            || e.EffectiveViewport.Height <= 0
            || control.DataContext is not RegistryPackageMediaItemViewModel media)
        {
            return;
        }

        control.EffectiveViewportChanged -= MediaFrame_OnEffectiveViewportChanged;
        media.LoadWhenVisible();
    }
}
