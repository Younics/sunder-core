using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

public partial class MainWindowViewModel
{
    private readonly LatestAsyncRequest _registryAuthRequest = new();

    [ObservableProperty]
    private bool _isRegistryAuthBusy;

    [ObservableProperty]
    private bool _isRegistrySignedIn;

    [ObservableProperty]
    private string _registryAccountDisplayName = "Registry";

    [ObservableProperty]
    private string _registryAccountSubtitle = "Not signed in";

    [ObservableProperty]
    private string _registryAccountStatusText = "Sign in to publish packages and Stacks.";

    [ObservableProperty]
    private string _registryAccountAvatarText = "R";

    [ObservableProperty]
    private IBrush _registryAccountAvatarBrush = RegistryAccountPresentationState.SignedOut().AvatarBrush;

    [ObservableProperty]
    private string? _registryAccountAvatarUrl;

    public bool CanManageRegistryAccount => _registryAuthService is not null && !IsRegistryAuthBusy;

    public bool CanOpenRegistryHub => _externalBrowserService is not null && !IsRegistryAuthBusy;

    public bool ShowRegistrySignInButton => !IsRegistrySignedIn;

    public bool ShowRegistrySignOutButton => IsRegistrySignedIn;

    public string RegistryAccountButtonToolTip => IsRegistrySignedIn
        ? $"Sunder Account: {RegistryAccountDisplayName}"
        : "Sunder Account sign-in";

    partial void OnIsRegistryAuthBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanManageRegistryAccount));
        OnPropertyChanged(nameof(CanOpenRegistryHub));
        RefreshRegistryAccountCommand.NotifyCanExecuteChanged();
        LoginRegistryCommand.NotifyCanExecuteChanged();
        LogoutRegistryCommand.NotifyCanExecuteChanged();
        OpenRegistryHubCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRegistrySignedInChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowRegistrySignInButton));
        OnPropertyChanged(nameof(ShowRegistrySignOutButton));
        OnPropertyChanged(nameof(RegistryAccountButtonToolTip));
    }

    partial void OnRegistryAccountDisplayNameChanged(string value)
    {
        OnPropertyChanged(nameof(RegistryAccountButtonToolTip));
    }

    [RelayCommand(CanExecute = nameof(CanManageRegistryAccount))]
    private async Task RefreshRegistryAccountAsync()
    {
        if (_disposed || _registryAuthService is null)
        {
            return;
        }

        using var request = _registryAuthRequest.Start(_tasks.Token);
        IsRegistryAuthBusy = true;
        RegistryAuthState? cachedState = null;
        try
        {
            cachedState = _registryAuthService.GetCachedStatus();
            if (cachedState.IsSignedIn)
            {
                ApplyRegistryAuthState(cachedState);
            }

            var state = await _registryAuthService.GetStatusAsync(cancellationToken: request.Token);
            if (request.IsCurrent)
            {
                ApplyRegistryAuthState(state);
            }
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!request.IsCurrent)
            {
                return;
            }

            if (cachedState is { IsSignedIn: true })
            {
                RegistryAccountStatusText = "Signed in to Sunder. Account refresh failed.";
            }
            else
            {
                ApplySignedOutRegistryState(ex.Message);
            }
        }
        finally
        {
            if (request.IsCurrent)
            {
                IsRegistryAuthBusy = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageRegistryAccount))]
    private async Task LoginRegistryAsync()
    {
        if (_disposed || _registryAuthService is null)
        {
            return;
        }

        using var request = _registryAuthRequest.Start(_tasks.Token);
        IsRegistryAuthBusy = true;
        try
        {
            var state = await _registryAuthService.LoginAsync(cancellationToken: request.Token);
            if (request.IsCurrent)
            {
                ApplyRegistryAuthState(state);
            }
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (request.IsCurrent)
            {
                ApplySignedOutRegistryState(ex.Message);
            }
        }
        finally
        {
            if (request.IsCurrent)
            {
                IsRegistryAuthBusy = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageRegistryAccount))]
    private async Task LogoutRegistryAsync()
    {
        if (_disposed || _registryAuthService is null)
        {
            return;
        }

        using var request = _registryAuthRequest.Start(_tasks.Token);
        IsRegistryAuthBusy = true;
        try
        {
            var state = await _registryAuthService.LogoutAsync(cancellationToken: request.Token);
            if (request.IsCurrent)
            {
                ApplyRegistryAuthState(state);
            }
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
        }
        finally
        {
            if (request.IsCurrent)
            {
                IsRegistryAuthBusy = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenRegistryHub))]
    private void OpenRegistryHub()
    {
        if (_disposed || _externalBrowserService is null)
        {
            return;
        }

        try
        {
            _externalBrowserService.Open(RegistryUrlHelper.DefaultRegistryUrl);
        }
        catch (Exception ex)
        {
            RegistryAccountStatusText = $"Could not open Sunder Hub: {ex.Message}";
        }
    }

    private void ApplyRegistryAuthState(RegistryAuthState state)
        => ApplyRegistryAccountPresentation(RegistryAccountPresentationState.FromAuthState(state));

    private void ApplySignedOutRegistryState(string? message)
        => ApplyRegistryAccountPresentation(RegistryAccountPresentationState.SignedOut(message));

    private void ApplyRegistryAccountPresentation(RegistryAccountPresentationState state)
    {
        IsRegistrySignedIn = state.IsSignedIn;
        RegistryAccountDisplayName = state.DisplayName;
        RegistryAccountSubtitle = state.Subtitle;
        RegistryAccountStatusText = state.StatusText;
        RegistryAccountAvatarText = state.AvatarText;
        RegistryAccountAvatarBrush = state.AvatarBrush;
        RegistryAccountAvatarUrl = state.AvatarUrl;
    }
}
