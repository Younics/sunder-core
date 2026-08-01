namespace Sunder.Package.Hosting;

internal static class NativeLibraryFallbackResolver
{
    public static string? Resolve(
        string entryAssemblyPath,
        string unmanagedDllName,
        string runtimeIdentifier)
    {
        if (string.IsNullOrWhiteSpace(unmanagedDllName)
            || !string.Equals(Path.GetFileName(unmanagedDllName), unmanagedDllName, StringComparison.Ordinal))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(runtimeIdentifier)
            || runtimeIdentifier.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return null;
        }

        var runtimesDirectory = Path.Combine(Path.GetDirectoryName(entryAssemblyPath)!, "runtimes");
        if (!Directory.Exists(runtimesDirectory))
        {
            return null;
        }

        var runtimeDirectories = Directory.EnumerateDirectories(runtimesDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(
                Path.GetFileName(path),
                runtimeIdentifier,
                PathComparison))
            .OrderBy(static path => path, PathComparer)
            .ToArray();
        if (runtimeDirectories.Length > 1)
        {
            throw new InvalidOperationException(
                $"Native fallback for selected RID '{runtimeIdentifier}' is ambiguous because multiple matching runtime directories exist.");
        }
        if (runtimeDirectories.Length == 0)
        {
            return null;
        }

        var nativeDirectory = Path.Combine(runtimeDirectories[0], "native");
        if (!Directory.Exists(nativeDirectory))
        {
            return null;
        }

        var fileName = GetPlatformLibraryFileName(unmanagedDllName);
        var candidates = Directory.EnumerateFiles(nativeDirectory, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(path), fileName, PathComparison))
            .OrderBy(static path => path, PathComparer)
            .ToArray();
        return candidates.Length switch
        {
            0 => null,
            1 => candidates[0],
            _ => throw new InvalidOperationException(
                $"Native fallback for '{unmanagedDllName}' and selected RID '{runtimeIdentifier}' is ambiguous: "
                + string.Join(", ", candidates.Select(Path.GetFullPath)) + "."),
        };
    }

    internal static string GetPlatformLibraryFileName(string unmanagedDllName)
    {
        if (OperatingSystem.IsWindows())
        {
            return unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? unmanagedDllName
                : $"{unmanagedDllName}.dll";
        }
        if (OperatingSystem.IsMacOS())
        {
            if (unmanagedDllName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
            {
                return unmanagedDllName;
            }
            return $"{(unmanagedDllName.StartsWith("lib", StringComparison.Ordinal) ? string.Empty : "lib")}{unmanagedDllName}.dylib";
        }
        if (unmanagedDllName.EndsWith(".so", StringComparison.Ordinal)
            || unmanagedDllName.Contains(".so.", StringComparison.Ordinal))
        {
            return unmanagedDllName;
        }
        return $"{(unmanagedDllName.StartsWith("lib", StringComparison.Ordinal) ? string.Empty : "lib")}{unmanagedDllName}.so";
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
