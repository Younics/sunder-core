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
        if (_disposed)
        {
            return;
        }

        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _tasks.Token);
        cancellationToken = lifetimeCancellation.Token;
        IsBusy = true;
        try
        {
            var preferredStackId = SelectedStack?.StackId;
            await _stackLibrary.RefreshAsync(cancellationToken);
            RebuildStackList(preferredStackId);
            if (_stackLibrary.Count == 0)
            {
                StatusText = "No local Stacks yet. Import a .sunderstack file to start.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
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
        if (_disposed || SelectedRegistryStack is null || !TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return false;
        }

        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _tasks.Token);
        cancellationToken = lifetimeCancellation.Token;
        return await ImportRegistryStackAsync(SelectedRegistryStack.StackId, registryUrl, launchKind, cancellationToken) is not null;
    }

    [RelayCommand(CanExecute = nameof(CanImportStack))]
    private async Task ImportStackAsync()
        => await ImportStackWithPickerAsync();

    public async Task<bool> ImportStackWithPickerAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return false;
        }

        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _tasks.Token);
        cancellationToken = lifetimeCancellation.Token;
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
        var selectedStack = SelectedStack;
        if (_disposed || selectedStack is null)
        {
            return;
        }

        var cancellationToken = _tasks.Token;
        var path = await _archivePicker.PickStackSavePathAsync(selectedStack.StackId + ".sunderstack", cancellationToken);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _stackLibrary.ExportAsync(selectedStack.Item, path, cancellationToken);
            StatusText = $"Exported '{selectedStack.Name}'.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanPublishSelectedStack))]
    private async Task PublishSelectedStackAsync()
    {
        var selectedStack = SelectedStack;
        if (_disposed || selectedStack is null || !TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return;
        }

        var cancellationToken = _tasks.Token;
        IsBusy = true;
        try
        {
            var upload = await _runtimeApiClient.UploadStackAsync(selectedStack.LocalPath, cancellationToken);
            var result = await _runtimeApiClient.PublishRegistryStackAsync(new RuntimeRegistryPublishRequest(registryUrl.AbsoluteUri, upload.UploadId), cancellationToken);
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
                now,
                cancellationToken);
            await _stackLibrary.RefreshAsync(cancellationToken);
            RebuildStackList(selectedStack.StackId);
            StatusText = result.Message ?? $"Published Stack '{publishedStackId}'.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                NotifySelectionChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanUnpublishSelectedStack))]
    private async Task UnpublishSelectedStackAsync()
    {
        var selectedStack = SelectedStack;
        var publishedStackId = selectedStack?.PublishedStackId;
        if (_disposed || selectedStack is null || string.IsNullOrWhiteSpace(publishedStackId))
        {
            return;
        }

        if (!TryResolvePublishedRegistryUrl(selectedStack, out var registryUrl))
        {
            return;
        }

        var cancellationToken = _tasks.Token;
        IsBusy = true;
        try
        {
            var result = await _runtimeApiClient.DeleteRegistryStackAsync(new RuntimeRegistryDeleteStackRequest(registryUrl.AbsoluteUri, publishedStackId), cancellationToken);
            if (!result.Success)
            {
                StatusText = result.Forbidden
                    ? "Registry Stack unpublish requires sign-in as the Stack owner."
                    : result.Errors.FirstOrDefault() ?? "Registry Stack unpublish failed.";
                return;
            }

            await _library.ClearPublishStateAsync(selectedStack.StackId, cancellationToken);
            await _stackLibrary.RefreshAsync(cancellationToken);
            RebuildStackList(selectedStack.StackId);
            StatusText = result.Message ?? $"Unpublished Stack '{publishedStackId}'.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                NotifySelectionChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleSelectedStackStar))]
    private async Task ToggleSelectedStackStarAsync()
    {
        var selectedStack = SelectedStack;
        var publishedStackId = selectedStack?.PublishedStackId;
        if (_disposed || selectedStack is null || string.IsNullOrWhiteSpace(publishedStackId))
        {
            return;
        }

        if (!TryResolvePublishedRegistryUrl(selectedStack, out var registryUrl))
        {
            return;
        }

        var cancellationToken = _tasks.Token;
        IsBusy = true;
        try
        {
            var result = await _runtimeApiClient.SetRegistryStackStarAsync(
                new RuntimeRegistryStarRequest(registryUrl.AbsoluteUri, publishedStackId, !SelectedStackIsStarred),
                cancellationToken);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                NotifySelectionChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleSelectedRegistryStackStar))]
    private async Task ToggleSelectedRegistryStackStarAsync()
    {
        var selectedStack = SelectedRegistryStack;
        if (_disposed || selectedStack is null || !TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return;
        }

        var cancellationToken = _tasks.Token;
        IsBusy = true;
        try
        {
            var result = await _runtimeApiClient.SetRegistryStackStarAsync(
                new RuntimeRegistryStarRequest(registryUrl.AbsoluteUri, selectedStack.StackId, !SelectedRegistryStackIsStarred),
                cancellationToken);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                NotifyRegistrySelectionChanged();
                NotifyRegistryStackStateChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedRegistryStack))]
    private async Task DeleteSelectedRegistryStackAsync()
    {
        if (_disposed || SelectedRegistryStack is null || !TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return;
        }

        var selectedStack = SelectedRegistryStack;
        var cancellationToken = _tasks.Token;
        IsBusy = true;
        try
        {
            var result = await _runtimeApiClient.DeleteRegistryStackAsync(
                new RuntimeRegistryDeleteStackRequest(registryUrl.AbsoluteUri, selectedStack.StackId),
                cancellationToken);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                NotifyRegistryStackStateChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedStack))]
    private async Task RemoveSelectedStackAsync()
    {
        var selectedStack = SelectedStack;
        if (_disposed || selectedStack is null)
        {
            return;
        }

        var removedName = selectedStack.Name;
        var cancellationToken = _tasks.Token;
        IsBusy = true;
        try
        {
            await _stackLibrary.DeleteAsync(selectedStack.Item, cancellationToken);
            RebuildStackList();
            StatusText = $"Removed local Stack '{removedName}'.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }

    private async Task<LocalStackLibraryItem?> ImportStackFromPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return null;
        }

        IsBusy = true;
        try
        {
            var item = await _stackLibrary.ImportAsync(path, cancellationToken);
            RebuildStackList(item.StackId);
            StatusText = $"Imported '{item.Name}'. Review it before use.";
            return item;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return null;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
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
        if (_disposed)
        {
            return null;
        }

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
            var item = await _stackLibrary.ImportAsync(tempPath, cancellationToken);
            RebuildStackList(item.StackId);
            StatusText = launchKind == AppLaunchRequestKind.StackUse
                ? $"Downloaded '{item.Name}' from the Registry. Review it before use."
                : $"Downloaded '{item.Name}' from the Registry.";
            return item;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return null;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
            TryDeleteDirectory(tempDirectory);
        }
    }
}
