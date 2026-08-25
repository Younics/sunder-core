using System.Collections.ObjectModel;
using LiveMarkdown.Avalonia;
using Sunder.App.Services;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

internal sealed class MarketplacePackageProfileViewModel : IDisposable
{
    private readonly RemoteImageService? _remoteImageService;

    public MarketplacePackageProfileViewModel(RemoteImageService? remoteImageService = null)
    {
        _remoteImageService = remoteImageService;
    }

    public event Func<IReadOnlyList<RegistryPackageMediaItemViewModel>, int, Task>? ImageGalleryRequested;

    public ObservableStringBuilder ReadmeMarkdownBuilder { get; } = new();

    public ObservableCollection<RegistryPackageProfileLinkViewModel> Links { get; } = [];

    public ObservableCollection<RegistryPackageProfileMetadataItemViewModel> Metadata { get; } = [];

    public ObservableCollection<string> Tags { get; } = [];

    public ObservableCollection<RegistryPackageMediaItemViewModel> Media { get; } = [];

    public bool HasReadme => ReadmeMarkdownBuilder.Length > 0;

    public bool HasLinks => Links.Count > 0;

    public bool HasMetadata => Metadata.Count > 0;

    public bool HasTags => Tags.Count > 0;

    public bool HasProfile => HasLinks || HasMetadata || HasTags;

    public bool HasMedia => Media.Count > 0;

    public string? Apply(RegistryPackageProfile? profile, Uri? registryOrigin = null)
    {
        ReadmeMarkdownBuilder.Clear();
        if (!string.IsNullOrWhiteSpace(profile?.ReadmeMarkdown))
        {
            ReadmeMarkdownBuilder.Append(profile.ReadmeMarkdown);
        }

        Links.ReplaceWith(BuildProfileLinks(profile));
        Metadata.ReplaceWith(BuildProfileMetadata(profile));
        Tags.ReplaceWith(BuildProfileTags(profile));
        DisposeMedia();
        Media.ReplaceWith(BuildMedia(profile?.Media, registryOrigin));

        return string.IsNullOrWhiteSpace(profile?.ShortDescription)
            ? null
            : profile.ShortDescription;
    }

    public string? Apply(RegistryStackProfile? profile, Uri? registryOrigin = null)
    {
        ReadmeMarkdownBuilder.Clear();
        if (!string.IsNullOrWhiteSpace(profile?.ReadmeMarkdown))
        {
            ReadmeMarkdownBuilder.Append(profile.ReadmeMarkdown);
        }

        Links.Clear();
        Metadata.Clear();
        Tags.ReplaceWith(profile?.Tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? []);
        DisposeMedia();
        Media.ReplaceWith((profile?.Media ?? [])
            .OrderBy(media => media.SortOrder)
            .Where(media => IsAcceptedMediaUrl(media.Url, registryOrigin))
            .Take(RemoteImageService.MaxAcceptedMediaEntries)
            .Select(media => new RegistryPackageMediaItemViewModel(
                new RegistryPackageMedia(
                    media.MediaId,
                    media.FileName,
                    media.ContentType,
                    media.Size,
                    media.AltText,
                    media.SortOrder,
                    media.Url),
                OpenImageGalleryAsync,
                remoteImageService: _remoteImageService,
                registryOrigin: registryOrigin))
            .ToArray());

        return string.IsNullOrWhiteSpace(profile?.ShortDescription)
            ? null
            : profile.ShortDescription;
    }

    public void Dispose() => DisposeMedia();

    private async Task OpenImageGalleryAsync(RegistryPackageMediaItemViewModel media)
    {
        var items = Media.ToArray();
        var index = Array.IndexOf(items, media);
        var handler = ImageGalleryRequested;
        if (index < 0 || handler is null)
        {
            return;
        }

        await handler.Invoke(items, index);
    }

    private void DisposeMedia()
    {
        foreach (var media in Media)
        {
            media.Dispose();
        }
    }

    private IReadOnlyList<RegistryPackageMediaItemViewModel> BuildMedia(
        IReadOnlyList<RegistryPackageMedia>? media,
        Uri? registryOrigin)
        => (media ?? [])
            .OrderBy(item => item.SortOrder)
            .Where(item => IsAcceptedMediaUrl(item.Url, registryOrigin))
            .Take(RemoteImageService.MaxAcceptedMediaEntries)
            .Select(item => new RegistryPackageMediaItemViewModel(
                item,
                OpenImageGalleryAsync,
                remoteImageService: _remoteImageService,
                registryOrigin: registryOrigin))
            .ToArray();

    private static bool IsAcceptedMediaUrl(string url, Uri? registryOrigin)
        => registryOrigin is null
           || RemoteImageService.TryResolveRegistryImageUri(registryOrigin, url, out _);

    private static IReadOnlyList<RegistryPackageProfileLinkViewModel> BuildProfileLinks(RegistryPackageProfile? profile)
    {
        if (profile is null)
        {
            return [];
        }

        var links = new List<RegistryPackageProfileLinkViewModel>();
        AddProfileLink(links, "Website", profile.WebsiteUrl);
        AddProfileLink(links, "Source", profile.SourceUrl);
        AddProfileLink(links, "Issues", profile.IssueTrackerUrl);
        return links;
    }

    private static IReadOnlyList<RegistryPackageProfileMetadataItemViewModel> BuildProfileMetadata(RegistryPackageProfile? profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.License))
        {
            return [];
        }

        return [new RegistryPackageProfileMetadataItemViewModel("License", profile.License.Trim())];
    }

    private static IReadOnlyList<string> BuildProfileTags(RegistryPackageProfile? profile)
        => profile?.Tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

    private static void AddProfileLink(
        ICollection<RegistryPackageProfileLinkViewModel> links,
        string label,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var navigateUri))
        {
            return;
        }

        links.Add(new RegistryPackageProfileLinkViewModel(label, navigateUri));
    }
}
