using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed partial class StacksWindowViewModel
{
    private static string FormatStackStats(RegistryStackStats stats)
        => $"{FormatCount(stats.Stars, "star")} · {FormatCount(stats.TotalDownloads, "download")}";

    private static string FormatCount(long count, string singular)
        => $"{count:N0} {singular}{(count == 1 ? string.Empty : "s")}";

    private void NotifyRegistryStackStateChanged()
    {
        OnPropertyChanged(nameof(HasRegistryStacks));
        OnPropertyChanged(nameof(ShowNoRegistryStacks));
        OnPropertyChanged(nameof(CanSearchRegistryStacks));
        OnPropertyChanged(nameof(CanCreateStack));
        OnPropertyChanged(nameof(CanEditSelectedStack));
        OnPropertyChanged(nameof(CanImportSelectedRegistryStack));
        OnPropertyChanged(nameof(CanUseSelectedRegistryStack));
        OnPropertyChanged(nameof(CanDeleteSelectedRegistryStack));
        OnPropertyChanged(nameof(CanToggleSelectedRegistryStackStar));
        SearchRegistryStacksCommand.NotifyCanExecuteChanged();
        ImportSelectedRegistryStackCommand.NotifyCanExecuteChanged();
        DeleteSelectedRegistryStackCommand.NotifyCanExecuteChanged();
        ToggleSelectedRegistryStackStarCommand.NotifyCanExecuteChanged();
    }

    private void NotifyBrowserModeChanged()
    {
        OnPropertyChanged(nameof(IsLocalMode));
        OnPropertyChanged(nameof(IsMarketplaceMode));
        OnPropertyChanged(nameof(ShowNoStacks));
        OnPropertyChanged(nameof(ShowNoRegistryStacks));
    }

    private void NotifyRegistrySelectionChanged()
    {
        OnPropertyChanged(nameof(HasRegistrySelection));
        OnPropertyChanged(nameof(ShowRegistrySelectedDetails));
        OnPropertyChanged(nameof(ShowNoRegistrySelection));
        OnPropertyChanged(nameof(ShowRegistryStackDetailsLoading));
        OnPropertyChanged(nameof(ShowRegistryStackDetailsContent));
        OnPropertyChanged(nameof(ShowRegistryStackDetailsError));
        NotifyRegistrySelectedDetailsChanged();
    }

    private void NotifyRegistryStackDetailsStateChanged()
    {
        OnPropertyChanged(nameof(HasRegistryStackDetailsError));
        OnPropertyChanged(nameof(ShowRegistryStackDetailsSpinner));
        OnPropertyChanged(nameof(ShowRegistryStackDetailsLoading));
        OnPropertyChanged(nameof(ShowRegistryStackDetailsContent));
        OnPropertyChanged(nameof(ShowRegistryStackDetailsError));
        NotifyRegistrySelectedDetailsChanged();
        NotifyRegistryStackStateChanged();
    }

    private void NotifyRegistrySelectedDetailsChanged()
    {
        OnPropertyChanged(nameof(HasRegistrySelectedSummary));
        OnPropertyChanged(nameof(ShowRegistryStackDetailsContent));
        OnPropertyChanged(nameof(HasRegistrySelectedPackages));
        OnPropertyChanged(nameof(HasRegistrySelectedFragments));
        OnPropertyChanged(nameof(HasRegistrySelectedRequiredInputs));
        OnPropertyChanged(nameof(ShowRegistryStackStats));
        OnPropertyChanged(nameof(ShowSelectedRegistryStackStarAction));
        NotifyRegistryProfileChanged();
    }

    private void NotifyRegistryProfileChanged()
    {
        OnPropertyChanged(nameof(HasRegistryReadme));
        OnPropertyChanged(nameof(HasRegistryProfileLinks));
        OnPropertyChanged(nameof(HasRegistryProfileMetadata));
        OnPropertyChanged(nameof(HasRegistryProfileTags));
        OnPropertyChanged(nameof(HasRegistryProfile));
        OnPropertyChanged(nameof(HasRegistryProfileMedia));
        OnPropertyChanged(nameof(HasRegistryAttributions));
        OnPropertyChanged(nameof(HasRegistryCreators));
        OnPropertyChanged(nameof(HasRegistryMaintainers));
    }

    private void DisposeSelectedLocalDetails()
    {
        foreach (var detail in SelectedLocalDetails)
        {
            detail.Dispose();
        }

        SelectedLocalDetails.Clear();
    }

    private void DisposeRegistrySelectedDetails()
    {
        foreach (var detail in RegistrySelectedDetails)
        {
            detail.Dispose();
        }

        RegistrySelectedDetails.Clear();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ShowSelectedDetails));
        OnPropertyChanged(nameof(ShowPublishSelectedStack));
        OnPropertyChanged(nameof(ShowUnpublishSelectedStack));
        OnPropertyChanged(nameof(ShowSelectedStackStats));
        OnPropertyChanged(nameof(ShowSelectedStackStarAction));
        OnPropertyChanged(nameof(ShowNoSelection));
        OnPropertyChanged(nameof(CanEditSelectedStack));
        NotifyCommandStateChanged();
    }

    private void NotifySelectedDetailsChanged()
    {
        OnPropertyChanged(nameof(HasSelectedSummary));
        OnPropertyChanged(nameof(HasSelectedReadme));
        OnPropertyChanged(nameof(HasSelectedProfileMedia));
        OnPropertyChanged(nameof(HasSelectedPackages));
        OnPropertyChanged(nameof(HasSelectedLocalDetails));
        OnPropertyChanged(nameof(HasSelectedFragments));
        OnPropertyChanged(nameof(ShowSelectedFragments));
        OnPropertyChanged(nameof(HasSelectedRequiredInputs));
        OnPropertyChanged(nameof(ShowPublishSelectedStack));
        OnPropertyChanged(nameof(ShowUnpublishSelectedStack));
        OnPropertyChanged(nameof(ShowSelectedStackStats));
        OnPropertyChanged(nameof(ShowSelectedStackStarAction));
    }

    private void NotifyCommandStateChanged()
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanImportStack));
        OnPropertyChanged(nameof(CanSearchRegistryStacks));
        OnPropertyChanged(nameof(CanEditSelectedStack));
        OnPropertyChanged(nameof(CanImportSelectedRegistryStack));
        OnPropertyChanged(nameof(CanUseSelectedRegistryStack));
        OnPropertyChanged(nameof(CanUseSelectedStack));
        OnPropertyChanged(nameof(CanDeleteSelectedRegistryStack));
        OnPropertyChanged(nameof(CanExportSelectedStack));
        OnPropertyChanged(nameof(CanPublishSelectedStack));
        OnPropertyChanged(nameof(CanUnpublishSelectedStack));
        OnPropertyChanged(nameof(CanToggleSelectedStackStar));
        OnPropertyChanged(nameof(CanToggleSelectedRegistryStackStar));
        OnPropertyChanged(nameof(CanRemoveSelectedStack));
        RefreshCommand.NotifyCanExecuteChanged();
        ImportStackCommand.NotifyCanExecuteChanged();
        SearchRegistryStacksCommand.NotifyCanExecuteChanged();
        ImportSelectedRegistryStackCommand.NotifyCanExecuteChanged();
        DeleteSelectedRegistryStackCommand.NotifyCanExecuteChanged();
        ExportSelectedStackCommand.NotifyCanExecuteChanged();
        PublishSelectedStackCommand.NotifyCanExecuteChanged();
        UnpublishSelectedStackCommand.NotifyCanExecuteChanged();
        ToggleSelectedStackStarCommand.NotifyCanExecuteChanged();
        ToggleSelectedRegistryStackStarCommand.NotifyCanExecuteChanged();
        RemoveSelectedStackCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<string> GetSelectedFragmentIds()
        => SelectedFragments
            .Where(fragment => fragment.IsSelected)
            .Select(fragment => fragment.FragmentId)
            .ToArray();

    private static RegistryStackProfile ToRegistryStackProfile(LocalStackLibraryItem item)
        => new(
            item.StackId,
            item.Summary,
            item.ReadmeMarkdown,
            WebsiteUrl: null,
            SourceUrl: null,
            IssueTrackerUrl: null,
            License: null,
            Tags: [],
            Media: (item.Media ?? [])
                .OrderBy(media => media.SortOrder)
                .Select(media => new RegistryStackMedia(
                    Guid.NewGuid(),
                    media.FileName,
                    media.ContentType,
                    media.Size,
                    media.AltText,
                    media.SortOrder,
                    new Uri(media.LocalPath).AbsoluteUri))
                .ToArray(),
            item.UpdatedAtUtc);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Temporary Registry Stack download cleanup is best effort.
        }
    }
}
