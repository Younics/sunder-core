namespace Sunder.Package.Build.Tasks;

internal static class ManagedAssemblyPath
{
    public static bool IsCandidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }
}
