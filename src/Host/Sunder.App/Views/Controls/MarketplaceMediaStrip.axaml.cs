using System.Collections;
using Avalonia;
using Avalonia.Controls;

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
}
