using Sunder.Sdk.Packaging;

namespace Sunder.App.ViewModels;

internal static class RegistryPackageVersionOrdering
{
    public static IComparer<string> Comparer { get; } = new VersionComparer();

    private sealed class VersionComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (string.Equals(x, y, StringComparison.Ordinal))
            {
                return 0;
            }

            if (!SemanticVersion.TryParse(x, out var left))
            {
                return SemanticVersion.TryParse(y, out _) ? -1 : string.CompareOrdinal(x, y);
            }

            if (!SemanticVersion.TryParse(y, out var right))
            {
                return 1;
            }

            var precedence = left.CompareTo(right);
            return precedence != 0 ? precedence : string.CompareOrdinal(x, y);
        }
    }
}
