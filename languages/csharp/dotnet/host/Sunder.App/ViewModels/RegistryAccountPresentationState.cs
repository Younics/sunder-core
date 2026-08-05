using Avalonia.Media;
using Sunder.App.Services;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

internal sealed record RegistryAccountPresentationState(
    bool IsSignedIn,
    string DisplayName,
    string Subtitle,
    string StatusText,
    string AvatarText,
    IBrush AvatarBrush,
    string? AvatarUrl)
{
    private static readonly Color[] AvatarColors =
    [
        Color.FromRgb(0xD9, 0x9A, 0x3A),
        Color.FromRgb(0xB6, 0x7A, 0x2B),
        Color.FromRgb(0x8D, 0x78, 0x56),
        Color.FromRgb(0xA0, 0x84, 0x5C),
        Color.FromRgb(0xC2, 0x92, 0x4A),
    ];

    public static RegistryAccountPresentationState FromAuthState(RegistryAuthState state)
    {
        if (!state.IsSignedIn || state.User is null)
        {
            return SignedOut(state.Message);
        }

        var displayName = GetDisplayName(state.User);
        var avatarLabel = FirstNonEmpty(
            FormatUsername(state.User.Username),
            SafeDisplayName(state.User.DisplayName),
            state.User.Email) ?? "registry";
        return new RegistryAccountPresentationState(
            true,
            displayName,
            state.RegistryUrl.Host,
            "Signed in to Sunder.",
            BuildAvatarText(avatarLabel),
            new SolidColorBrush(PickAvatarColor(avatarLabel)),
            NormalizeAvatarUrl(state.User.AvatarUrl));
    }

    public static RegistryAccountPresentationState SignedOut(string? message = null)
        => new(
            false,
            "Registry",
            "Not signed in",
            string.IsNullOrWhiteSpace(message) ? "Sign in to publish packages and Stacks." : message,
            "R",
            new SolidColorBrush(AvatarColors[0]),
            null);

    private static string GetDisplayName(RegistryCurrentUserResponse user)
        => FirstNonEmpty(FormatUsername(user.Username), SafeDisplayName(user.DisplayName), user.Email) ?? "Sunder user";

    private static string? FormatUsername(string? username)
        => string.IsNullOrWhiteSpace(username) ? null : $"@{username.Trim()}";

    private static string? SafeDisplayName(string? displayName)
    {
        var normalized = displayName?.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith("user_", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string BuildAvatarText(string displayName)
    {
        var label = displayName.Trim();
        label = label.StartsWith('@')
            ? label.TrimStart('@')
            : label.Contains('@', StringComparison.Ordinal)
                ? label.Split('@', 2)[0]
                : label;
        var parts = label
            .Split([' ', '.', '-', '_', '@'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0)
            .ToArray();
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => string.Concat(parts[0][..1], parts[^1][..1]).ToUpperInvariant(),
        };
    }

    private static Color PickAvatarColor(string seed)
    {
        var hash = 0;
        foreach (var character in seed)
        {
            hash = unchecked((hash * 31) + character);
        }

        return AvatarColors[(int)((uint)hash % AvatarColors.Length)];
    }

    private static string? NormalizeAvatarUrl(string? avatarUrl)
    {
        var normalized = avatarUrl?.Trim();
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? normalized
            : null;
    }
}
