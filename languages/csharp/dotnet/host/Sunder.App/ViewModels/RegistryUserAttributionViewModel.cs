using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

public sealed class RegistryUserAttributionViewModel(RegistryUserAttribution attribution)
{
    public string Role { get; } = attribution.IsOwner ? "Owner" : "Maintainer";

    public string DisplayName { get; } = string.IsNullOrWhiteSpace(attribution.Username)
        ? attribution.DisplayName ?? "Registry user"
        : $"@{attribution.Username}";

    public string? AvatarUrl { get; } = attribution.AvatarUrl;
}
