using System.Globalization;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

public sealed class RegistryUserAttributionViewModel(RegistryUserAttribution attribution, bool isCreator = false)
{
    public string Role { get; } = isCreator ? "Creator" : attribution.IsOwner ? "Owner" : "Maintainer";

    public string DisplayName { get; } = !string.IsNullOrWhiteSpace(attribution.Username) ? $"@{attribution.Username}"
        : string.IsNullOrWhiteSpace(attribution.DisplayName) ? "Registry user" : attribution.DisplayName.Trim();

    public string? AvatarUrl { get; } = attribution.AvatarUrl;
    public string Initial => StringInfo.GetNextTextElement(DisplayName.TrimStart('@')).ToUpperInvariant();

    internal static IEnumerable<RegistryUserAttributionViewModel> People(
        RegistryUserAttribution? creator, IEnumerable<RegistryUserAttribution> maintainers)
    {
        if (creator is not null) yield return new(creator, isCreator: true);
        foreach (var maintainer in maintainers)
        {
            if (creator == maintainer || (creator?.Username is { Length: > 0 } username
                && string.Equals(maintainer.Username, username, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            yield return new(maintainer);
        }
    }
}
