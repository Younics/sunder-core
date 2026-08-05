using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace Sunder.App.Views.Controls;

public partial class MarketplaceProfileCard : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<MarketplaceProfileCard, string>(nameof(Title), "Profile");
    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<MarketplaceProfileCard, string>(nameof(Description), string.Empty);
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

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public IEnumerable? Links { get => GetValue(LinksProperty); set => SetValue(LinksProperty, value); }
    public IEnumerable? Metadata { get => GetValue(MetadataProperty); set => SetValue(MetadataProperty, value); }
    public IEnumerable? Tags { get => GetValue(TagsProperty); set => SetValue(TagsProperty, value); }
    public bool HasLinks { get => GetValue(HasLinksProperty); set => SetValue(HasLinksProperty, value); }
    public bool HasMetadata { get => GetValue(HasMetadataProperty); set => SetValue(HasMetadataProperty, value); }
    public bool HasTags { get => GetValue(HasTagsProperty); set => SetValue(HasTagsProperty, value); }
}
