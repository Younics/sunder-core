using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace Sunder.App.Views.Controls;

public partial class StackDetailTree : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<StackDetailTree, IEnumerable?>(nameof(Items));

    public StackDetailTree()
    {
        InitializeComponent();
    }

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }
}
