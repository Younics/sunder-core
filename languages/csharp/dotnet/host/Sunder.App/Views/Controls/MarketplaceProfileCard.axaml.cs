using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace Sunder.App.Views.Controls;

public partial class MarketplaceProfileCard : UserControl
{
    public static readonly StyledProperty<IEnumerable?> LinksProperty =
        AvaloniaProperty.Register<MarketplaceProfileCard, IEnumerable?>(nameof(Links));
    public static readonly StyledProperty<IEnumerable?> MetadataProperty =
        AvaloniaProperty.Register<MarketplaceProfileCard, IEnumerable?>(nameof(Metadata));
    public static readonly StyledProperty<IEnumerable?> TagsProperty =
        AvaloniaProperty.Register<MarketplaceProfileCard, IEnumerable?>(nameof(Tags));
    public static readonly StyledProperty<bool> HasLinksProperty =
        AvaloniaProperty.Register<MarketplaceProfileCard, bool>(nameof(HasLinks));
    public static readonly StyledProperty<bool> HasMetadataProperty =
        AvaloniaProperty.Register<MarketplaceProfileCard, bool>(nameof(HasMetadata));
    public static readonly StyledProperty<bool> HasTagsProperty =
        AvaloniaProperty.Register<MarketplaceProfileCard, bool>(nameof(HasTags));

    public MarketplaceProfileCard()
    {
        InitializeComponent();
    }

    public IEnumerable? Links { get => GetValue(LinksProperty); set => SetValue(LinksProperty, value); }
    public IEnumerable? Metadata { get => GetValue(MetadataProperty); set => SetValue(MetadataProperty, value); }
    public IEnumerable? Tags { get => GetValue(TagsProperty); set => SetValue(TagsProperty, value); }
    public bool HasLinks { get => GetValue(HasLinksProperty); set => SetValue(HasLinksProperty, value); }
    public bool HasMetadata { get => GetValue(HasMetadataProperty); set => SetValue(HasMetadataProperty, value); }
    public bool HasTags { get => GetValue(HasTagsProperty); set => SetValue(HasTagsProperty, value); }
}
