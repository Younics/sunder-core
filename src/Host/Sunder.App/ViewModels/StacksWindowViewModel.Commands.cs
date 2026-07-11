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

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (IsMarketplaceMode)
        {
            await SearchRegistryStacksAsync();
            return;
        }

        await RefreshLocalStacksAsync();
    }

    private async Task RefreshLocalStacksAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var preferredStackId = SelectedStack?.StackId;
            _allStacks = await _library.ListAsync(cancellationToken);
            RebuildStackList(preferredStackId);
            if (_allStacks.Count == 0)
            {
                StatusText = "No local Stacks yet. Import a .sunderstack file to start.";
            }
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ShowLocal()
    {
        BrowserMode = StackBrowserMode.Local;
    }

    [RelayCommand]
    private void ShowMarketplace()
    {
        BrowserMode = StackBrowserMode.Marketplace;
        if (RegistryStacks.Count == 0 && HasRegistryUrlText)
        {
            QueueRegistrySearch(TimeSpan.Zero);
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    [RelayCommand]
    private void ClearRegistrySearch()
    {
        if (!string.IsNullOrEmpty(RegistrySearchText))
        {
            RegistrySearchText = string.Empty;
            return;
        }

        if (IsMarketplaceMode && HasRegistryUrlText)
        {
            QueueRegistrySearch(TimeSpan.Zero);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSearchRegistryStacks))]
    private async Task SearchRegistryStacksAsync()
    {
        CancelQueuedRegistrySearch();
        await SearchRegistryStacksCoreAsync();
    }

    private async Task SearchRegistryStacksCoreAsync(CancellationToken cancellationToken = default)
    {
        if (!TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return;
        }

        using var request = _registrySearchRequest.Start(cancellationToken);
        cancellationToken = request.Token;
        var searchVersion = ++_registrySearchVersion;
        IsBusy = true;
        SelectedRegistryStack = null;
        RegistryStacks.Clear();
        NotifyRegistryStackStateChanged();
        try
        {
            StatusText = "Searching Registry Stacks...";
            using var registryClient = _registryClientFactory(registryUrl);
            var query = string.IsNullOrWhiteSpace(RegistrySearchText) ? null : RegistrySearchText.Trim();
            var results = await registryClient.SearchStacksAsync(
                query,
                0,
                50,
                SelectedRegistrySortOption?.Sort ?? RegistrySearchSort.Downloads,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (searchVersion != _registrySearchVersion || !request.IsCurrent)
            {
                return;
            }

            foreach (var stack in results)
            {
                RegistryStacks.Add(new RegistryStackSearchItemViewModel(stack));
            }

            SelectedRegistryStack = RegistryStacks.FirstOrDefault();
            StatusText = RegistryStacks.Count == 0
                ? "No Registry Stacks matched the search."
                : $"Found {RegistryStacks.Count} Registry Stack{(RegistryStacks.Count == 1 ? string.Empty : "s")}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (searchVersion == _registrySearchVersion && request.IsCurrent)
            {
                StatusText = ex.Message;
            }
        }
        finally
        {
            if (searchVersion == _registrySearchVersion && request.IsCurrent)
            {
                IsBusy = false;
                NotifyRegistryStackStateChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportSelectedRegistryStack))]
    private async Task ImportSelectedRegistryStackAsync()
        => await ImportSelectedRegistryStackAsLocalAsync(AppLaunchRequestKind.StackDetails);

    public async Task<bool> ImportSelectedRegistryStackAsLocalAsync(
        AppLaunchRequestKind launchKind = AppLaunchRequestKind.StackDetails,
        CancellationToken cancellationToken = default)
    {
        if (SelectedRegistryStack is null || !TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return false;
        }

        return await ImportRegistryStackAsync(SelectedRegistryStack.StackId, registryUrl, launchKind, cancellationToken) is not null;
    }

    [RelayCommand(CanExecute = nameof(CanImportStack))]
    private async Task ImportStackAsync()
        => await ImportStackWithPickerAsync();

    public async Task<bool> ImportStackWithPickerAsync(CancellationToken cancellationToken = default)
    {
        var path = await _archivePicker.PickStackPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return await ImportStackFromPathAsync(path, cancellationToken) is not null;
    }

    [RelayCommand(CanExecute = nameof(CanExportSelectedStack))]
    private async Task ExportSelectedStackAsync()
    {
        if (SelectedStack is null)
        {
            return;
        }

        var path = await _archivePicker.PickStackSavePathAsync(SelectedStack.StackId + ".sunderstack");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _library.ExportAsync(SelectedStack.Item, path);
            StatusText = $"Exported '{SelectedStack.Name}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPublishSelectedStack))]
    private async Task PublishSelectedStackAsync()
    {
        var selectedStack = SelectedStack;
        if (selectedStack is null || !TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var upload = await _runtimeApiClient.UploadStackAsync(selectedStack.LocalPath);
            var result = await _runtimeApiClient.PublishRegistryStackAsync(new RuntimeRegistryPublishRequest(registryUrl.AbsoluteUri, upload.UploadId));
            if (!result.Success)
            {
                StatusText = result.Errors.FirstOrDefault() ?? "Registry Stack publish failed.";
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var publishedStackId = string.IsNullOrWhiteSpace(result.StackId) ? selectedStack.StackId : result.StackId!;
            await _library.UpdatePublishStateAsync(
                selectedStack.StackId,
                registryUrl.ToString(),
                publishedStackId,
                now,
                now);
            _allStacks = await _library.ListAsync();
            RebuildStackList(selectedStack.StackId);
            StatusText = result.Message ?? $"Published Stack '{publishedStackId}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifySelectionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUnpublishSelectedStack))]
    private async Task UnpublishSelectedStackAsync()
    {
        var selectedStack = SelectedStack;
        var publishedStackId = selectedStack?.PublishedStackId;
        if (selectedStack is null || string.IsNullOrWhiteSpace(publishedStackId))
        {
            return;
        }

        if (!TryResolvePublishedRegistryUrl(selectedStack, out var registryUrl))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _runtimeApiClient.DeleteRegistryStackAsync(new RuntimeRegistryDeleteStackRequest(registryUrl.AbsoluteUri, publishedStackId));
            if (!result.Success)
            {
                StatusText = result.Forbidden
                    ? "Registry Stack unpublish requires sign-in as the Stack owner."
                    : result.Errors.FirstOrDefault() ?? "Registry Stack unpublish failed.";
                return;
            }

            await _library.ClearPublishStateAsync(selectedStack.StackId);
            _allStacks = await _library.ListAsync();
            RebuildStackList(selectedStack.StackId);
            StatusText = result.Message ?? $"Unpublished Stack '{publishedStackId}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifySelectionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleSelectedStackStar))]
    private async Task ToggleSelectedStackStarAsync()
    {
        var selectedStack = SelectedStack;
        var publishedStackId = selectedStack?.PublishedStackId;
        if (selectedStack is null || string.IsNullOrWhiteSpace(publishedStackId))
        {
            return;
        }

        if (!TryResolvePublishedRegistryUrl(selectedStack, out var registryUrl))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _runtimeApiClient.SetRegistryStackStarAsync(
                new RuntimeRegistryStarRequest(registryUrl.AbsoluteUri, publishedStackId, !SelectedStackIsStarred));
            if (!result.Success)
            {
                StatusText = result.Forbidden
                    ? "Sign in to the Registry before starring a Stack."
                    : result.Errors.FirstOrDefault() ?? "Registry Stack star update failed.";
                return;
            }

            ApplySelectedStackStats(result.Stats);
            StatusText = result.Message ?? "Updated Stack star.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifySelectionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleSelectedRegistryStackStar))]
    private async Task ToggleSelectedRegistryStackStarAsync()
    {
        var selectedStack = SelectedRegistryStack;
        if (selectedStack is null || !TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _runtimeApiClient.SetRegistryStackStarAsync(
                new RuntimeRegistryStarRequest(registryUrl.AbsoluteUri, selectedStack.StackId, !SelectedRegistryStackIsStarred));
            if (!result.Success)
            {
                StatusText = result.Forbidden
                    ? "Sign in to the Registry before starring a Stack."
                    : result.Errors.FirstOrDefault() ?? "Registry Stack star update failed.";
                return;
            }

            ApplySelectedRegistryStackStats(result.Stats);
            StatusText = result.Message ?? "Updated Stack star.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyRegistrySelectionChanged();
            NotifyRegistryStackStateChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedRegistryStack))]
    private async Task DeleteSelectedRegistryStackAsync()
    {
        if (SelectedRegistryStack is null || !TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return;
        }

        var selectedStack = SelectedRegistryStack;
        IsBusy = true;
        try
        {
            var result = await _runtimeApiClient.DeleteRegistryStackAsync(new RuntimeRegistryDeleteStackRequest(registryUrl.AbsoluteUri, selectedStack.StackId));
            if (!result.Success)
            {
                StatusText = result.Forbidden
                    ? "Registry Stack deletion requires sign-in as the Stack owner."
                    : result.Errors.FirstOrDefault() ?? "Registry Stack deletion failed.";
                return;
            }

            RegistryStacks.Remove(selectedStack);
            SelectedRegistryStack = RegistryStacks.FirstOrDefault();
            StatusText = result.Message ?? $"Deleted Registry Stack '{selectedStack.StackId}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyRegistryStackStateChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedStack))]
    private async Task RemoveSelectedStackAsync()
    {
        if (SelectedStack is null)
        {
            return;
        }

        var removedName = SelectedStack.Name;
        IsBusy = true;
        try
        {
            await _library.DeleteAsync(SelectedStack.Item);
            _allStacks = await _library.ListAsync();
            RebuildStackList();
            StatusText = $"Removed local Stack '{removedName}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<LocalStackLibraryItem?> ImportStackFromPathAsync(string path, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var item = await _library.ImportAsync(path, cancellationToken);
            _allStacks = await _library.ListAsync(cancellationToken);
            RebuildStackList(item.StackId);
            StatusText = $"Imported '{item.Name}'. Review it before use.";
            return item;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ShowLinkedRegistryStackAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.StackId))
        {
            return;
        }

        if (request.RegistryUrl is not null)
        {
            RegistryUrlText = request.RegistryUrl.ToString();
        }

        BrowserMode = StackBrowserMode.Marketplace;
        RegistrySearchText = request.StackId;
        CancelQueuedRegistrySearch();
        if (!TryResolveRegistryUrlForRegistryAction(out _))
        {
            RegistryStacks.Clear();
            SelectedRegistryStack = null;
            NotifyRegistryStackStateChanged();
            return;
        }

        await SearchRegistryStacksCoreAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedStack = RegistryStacks.FirstOrDefault(stack =>
            string.Equals(stack.StackId, request.StackId, StringComparison.OrdinalIgnoreCase));
        if (selectedStack is null)
        {
            RegistryStacks.Clear();
            SelectedRegistryStack = null;
            NotifyRegistryStackStateChanged();
            StatusText = $"Registry Stack '{request.StackId}' was not found.";
            return;
        }

        KeepOnlyRegistryStack(selectedStack);
        SelectedRegistryStack = selectedStack;
        StatusText = $"Loaded {selectedStack.StackId}.";
    }

    private void KeepOnlyRegistryStack(RegistryStackSearchItemViewModel stack)
    {
        for (var index = RegistryStacks.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(RegistryStacks[index], stack))
            {
                continue;
            }

            RegistryStacks.RemoveAt(index);
        }

        NotifyRegistryStackStateChanged();
    }

    private async Task<LocalStackLibraryItem?> ImportRegistryStackAsync(
        string stackId,
        Uri registryUrl,
        AppLaunchRequestKind launchKind,
        CancellationToken cancellationToken = default)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "V1", "registry", Guid.NewGuid().ToString("N"));
        var tempPath = Path.Combine(tempDirectory, SunderStackFormat.BuildStackFileName(stackId));
        IsBusy = true;
        try
        {
            Directory.CreateDirectory(tempDirectory);
            StatusText = $"Downloading Registry Stack '{stackId}'...";
            using var registryClient = _registryClientFactory(registryUrl);
            var stack = await registryClient.GetStackAsync(stackId, cancellationToken);
            if (stack is null)
            {
                StatusText = $"Registry Stack '{stackId}' was not found.";
                return null;
            }

            await registryClient.DownloadStackAsync(stack.Artifact, stack.StackId, tempPath, cancellationToken);
            var item = await _library.ImportAsync(tempPath, cancellationToken);
            _allStacks = await _library.ListAsync(cancellationToken);
            RebuildStackList(item.StackId);
            StatusText = launchKind == AppLaunchRequestKind.StackUse
                ? $"Downloaded '{item.Name}' from the Registry. Review it before use."
                : $"Downloaded '{item.Name}' from the Registry.";
            return item;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
            TryDeleteDirectory(tempDirectory);
        }
    }
}
