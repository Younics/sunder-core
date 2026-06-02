using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Registry.Shared;

namespace Sunder.App.ViewModels;

public partial class MainWindowViewModel
{
    private static readonly Color[] RegistryAvatarColors =
    [
        Color.FromRgb(0xD9, 0x9A, 0x3A),
        Color.FromRgb(0xB6, 0x7A, 0x2B),
        Color.FromRgb(0x8D, 0x78, 0x56),
        Color.FromRgb(0xA0, 0x84, 0x5C),
        Color.FromRgb(0xC2, 0x92, 0x4A),
    ];

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
    private IBrush _registryAccountAvatarBrush = new SolidColorBrush(RegistryAvatarColors[0]);

    public bool CanManageRegistryAccount => _registryAuthService is not null && !IsRegistryAuthBusy;

    public bool ShowRegistrySignInButton => !IsRegistrySignedIn;

    public bool ShowRegistrySignOutButton => IsRegistrySignedIn;

    public string RegistryAccountButtonToolTip => IsRegistrySignedIn
        ? $"Sunder Account: {RegistryAccountDisplayName}"
        : "Sunder Account sign-in";

    partial void OnIsRegistryAuthBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanManageRegistryAccount));
        RefreshRegistryAccountCommand.NotifyCanExecuteChanged();
        LoginRegistryCommand.NotifyCanExecuteChanged();
        LogoutRegistryCommand.NotifyCanExecuteChanged();
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
        if (_registryAuthService is null)
        {
            return;
        }

        IsRegistryAuthBusy = true;
        try
        {
            ApplyRegistryAuthState(await _registryAuthService.GetStatusAsync());
        }
        catch (Exception ex)
        {
            ApplySignedOutRegistryState(ex.Message);
        }
        finally
        {
            IsRegistryAuthBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageRegistryAccount))]
    private async Task LoginRegistryAsync()
    {
        if (_registryAuthService is null)
        {
            return;
        }

        IsRegistryAuthBusy = true;
        try
        {
            ApplyRegistryAuthState(await _registryAuthService.LoginAsync());
        }
        catch (Exception ex)
        {
            ApplySignedOutRegistryState(ex.Message);
        }
        finally
        {
            IsRegistryAuthBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageRegistryAccount))]
    private void LogoutRegistry()
    {
        if (_registryAuthService is null)
        {
            return;
        }

        IsRegistryAuthBusy = true;
        try
        {
            ApplyRegistryAuthState(_registryAuthService.Logout());
        }
        finally
        {
            IsRegistryAuthBusy = false;
        }
    }

    private void ApplyRegistryAuthState(RegistryAuthState state)
    {
        if (!state.IsSignedIn || state.User is null)
        {
            ApplySignedOutRegistryState(state.Message);
            return;
        }

        IsRegistrySignedIn = true;
        RegistryAccountDisplayName = GetRegistryDisplayName(state.User);
        RegistryAccountSubtitle = state.RegistryUrl.Host;
        RegistryAccountStatusText = "Signed in to Sunder.";
        RegistryAccountAvatarText = BuildRegistryAvatarText(GetRegistryAvatarLabel(state.User));
        RegistryAccountAvatarBrush = new SolidColorBrush(PickRegistryAvatarColor(GetRegistryAvatarSeed(state.User)));
    }

    private void ApplySignedOutRegistryState(string? message)
    {
        IsRegistrySignedIn = false;
        RegistryAccountDisplayName = "Registry";
        RegistryAccountSubtitle = "Not signed in";
        RegistryAccountStatusText = string.IsNullOrWhiteSpace(message)
            ? "Sign in to publish packages and Stacks."
            : message;
        RegistryAccountAvatarText = "R";
        RegistryAccountAvatarBrush = new SolidColorBrush(RegistryAvatarColors[0]);
    }

    private static string GetRegistryDisplayName(RegistryCurrentUserResponse user)
        => FirstNonEmpty(user.Email, user.DisplayName) ?? "Sunder user";

    private static string GetRegistryAvatarLabel(RegistryCurrentUserResponse user)
        => FirstNonEmpty(user.Email, user.DisplayName) ?? "registry";

    private static string GetRegistryAvatarSeed(RegistryCurrentUserResponse user)
        => FirstNonEmpty(user.Email, user.DisplayName) ?? "registry";

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string BuildRegistryAvatarText(string displayName)
    {
        var label = displayName.Contains('@', StringComparison.Ordinal)
            ? displayName.Split('@', 2)[0]
            : displayName;
        var parts = label
            .Split([' ', '.', '-', '_', '@'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0)
            .ToArray();
        if (parts.Length == 0)
        {
            return "?";
        }

        return parts.Length == 1
            ? parts[0][..1].ToUpperInvariant()
            : string.Concat(parts[0][..1], parts[^1][..1]).ToUpperInvariant();
    }

    private static Color PickRegistryAvatarColor(string seed)
    {
        var hash = 0;
        foreach (var character in seed)
        {
            hash = unchecked((hash * 31) + character);
        }

        return RegistryAvatarColors[(int)((uint)hash % RegistryAvatarColors.Length)];
    }
}
