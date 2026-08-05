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
            var preferredStackId = Local.SelectedStack?.StackId;
            await Local.RefreshAsync(preferredStackId, cancellationToken);
            if (Local.Count == 0)
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
        if (Registry.Stacks.Count == 0 && HasRegistryUrlText)
        {
            QueueRegistrySearch(TimeSpan.Zero);
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        Local.SearchText = string.Empty;
    }

    [RelayCommand]
    private void ClearRegistrySearch()
    {
        if (!string.IsNullOrEmpty(Registry.SearchText))
        {
            Registry.SearchText = string.Empty;
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

        IsBusy = true;
        try
        {
            StatusText = "Searching Registry Stacks...";
            var result = await Registry.SearchAsync(cancellationToken);
            if (!result.Applied)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                StatusText = result.ErrorMessage;
                return;
            }

            StatusText = result.Count == 0
                ? "No Registry Stacks matched the search."
                : $"Found {result.Count} Registry Stack{(result.Count == 1 ? string.Empty : "s")}.";
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

    [RelayCommand(CanExecute = nameof(CanImportSelectedRegistryStack))]
    private async Task ImportSelectedRegistryStackAsync()
        => await ImportSelectedRegistryStackAsLocalAsync(AppLaunchRequestKind.StackDetails);

    public async Task<bool> ImportSelectedRegistryStackAsLocalAsync(
        AppLaunchRequestKind launchKind = AppLaunchRequestKind.StackDetails,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || Registry.SelectedStack is null || !TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return false;
        }

        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _tasks.Token);
        cancellationToken = lifetimeCancellation.Token;
        return await ImportRegistryStackAsync(Registry.SelectedStack.StackId, registryUrl, launchKind, cancellationToken) is not null;
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
        var selectedStack = Local.SelectedStack;
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
            await Local.ExportAsync(selectedStack, path, cancellationToken);
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
        var selectedStack = Local.SelectedStack;
        if (_disposed || selectedStack is null || !TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return;
        }

        var cancellationToken = _tasks.Token;
        IsBusy = true;
        try
        {
            var result = await _publishing.PublishAsync(
                selectedStack,
                registryUrl,
                preserveOriginalPublishTime: false,
                cancellationToken);
            StatusText = result.Message;
            if (result.Success)
            {
                await Local.RefreshAsync(selectedStack.StackId, cancellationToken);
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
                NotifySelectionChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanUnpublishSelectedStack))]
    private async Task UnpublishSelectedStackAsync()
    {
        var selectedStack = Local.SelectedStack;
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
            var result = await _publishing.UnpublishAsync(selectedStack, registryUrl, cancellationToken);
            StatusText = result.Message;
            if (result.Success)
            {
                await Local.RefreshAsync(selectedStack.StackId, cancellationToken);
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
                NotifySelectionChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleSelectedStackStar))]
    private async Task ToggleSelectedStackStarAsync()
    {
        var selectedStack = Local.SelectedStack;
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
            var result = await _publishing.ToggleStarAsync(registryUrl, publishedStackId, SelectedStackIsStarred, cancellationToken);
            ApplySelectedStackStats(result.Stats);
            StatusText = result.Message;
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
        var selectedStack = Registry.SelectedStack;
        if (_disposed || selectedStack is null || !TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return;
        }

        var cancellationToken = _tasks.Token;
        IsBusy = true;
        try
        {
            var result = await _publishing.ToggleStarAsync(registryUrl, selectedStack.StackId, SelectedRegistryStackIsStarred, cancellationToken);
            ApplySelectedRegistryStackStats(result.Stats);
            StatusText = result.Message;
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
        if (_disposed || Registry.SelectedStack is null || !TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return;
        }

        var selectedStack = Registry.SelectedStack;
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

            Registry.Stacks.Remove(selectedStack);
            Registry.SelectedStack = Registry.Stacks.FirstOrDefault();
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
        var selectedStack = Local.SelectedStack;
        if (_disposed || selectedStack is null)
        {
            return;
        }

        var removedName = selectedStack.Name;
        var cancellationToken = _tasks.Token;
        IsBusy = true;
        try
        {
            await Local.DeleteAsync(selectedStack, cancellationToken);
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
            var item = await Local.ImportAsync(path, cancellationToken);
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
            Registry.RegistryUrlText = request.RegistryUrl.ToString();
        }

        BrowserMode = StackBrowserMode.Marketplace;
        Registry.SearchText = request.StackId;
        CancelQueuedRegistrySearch();
        if (!TryResolveRegistryUrlForRegistryAction(out _))
        {
            Registry.Stacks.Clear();
            Registry.SelectedStack = null;
            NotifyRegistryStackStateChanged();
            return;
        }

        await SearchRegistryStacksCoreAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedStack = Registry.Stacks.FirstOrDefault(stack =>
            string.Equals(stack.StackId, request.StackId, StringComparison.OrdinalIgnoreCase));
        if (selectedStack is null)
        {
            Registry.Stacks.Clear();
            Registry.SelectedStack = null;
            NotifyRegistryStackStateChanged();
            StatusText = $"Registry Stack '{request.StackId}' was not found.";
            return;
        }

        KeepOnlyRegistryStack(selectedStack);
        Registry.SelectedStack = selectedStack;
        StatusText = $"Loaded {selectedStack.StackId}.";
    }

    private void KeepOnlyRegistryStack(RegistryStackSearchItemViewModel stack)
    {
        Registry.KeepOnly(stack);
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
            using var registryClient = Registry.CreateClient(registryUrl);
            var stack = await registryClient.GetStackAsync(stackId, cancellationToken);
            if (stack is null)
            {
                StatusText = $"Registry Stack '{stackId}' was not found.";
                return null;
            }

            await registryClient.DownloadStackAsync(stack.Artifact, stack.StackId, tempPath, cancellationToken);
            var item = await Local.ImportAsync(tempPath, cancellationToken);
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
