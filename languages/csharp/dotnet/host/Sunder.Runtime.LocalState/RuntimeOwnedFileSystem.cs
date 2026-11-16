namespace Sunder.Runtime.LocalState;

internal static partial class RuntimeOwnedFileSystem
{
    private const uint FileTypeMask = 0xF000;
    private const uint DirectoryType = 0x4000;
    private const uint RegularFileType = 0x8000;

    // Elevated Windows tokens may default new objects to the Administrators
    // group. Author the user owner at creation, never repair an existing owner.
    internal static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            CreateWindowsDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path);
        }
    }

    internal static FileStream CreateFile(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        int bufferSize,
        FileOptions options)
    {
        if (mode is not (FileMode.CreateNew or FileMode.OpenOrCreate))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsFile(path, mode, access, share, bufferSize, options);
        }
        return new FileStream(path, mode, access, share, bufferSize, options);
    }

    internal static void ValidateMutableDirectory(string mountRootPath, string path)
        => ValidateMutableEntry(mountRootPath, path, DirectoryType, requireSingleLink: false);

    internal static void ValidateMutableRegularFile(string mountRootPath, string path)
        => ValidateMutableEntry(mountRootPath, path, RegularFileType, requireSingleLink: true);

    internal static string GetMutableDirectoryIdentity(string path)
    {
        var fullPath = Path.GetFullPath(path);
        ValidateNoReparsePointAncestors(fullPath);
        return GetMutableDirectoryIdentity(fullPath, fullPath);
    }

    internal static string GetMutableDirectoryIdentity(string mountRootPath, string path)
        => GetMutableEntryIdentity(mountRootPath, path, DirectoryType, requireSingleLink: false);

    internal static string GetMutableRegularFileIdentity(string mountRootPath, string path)
        => GetMutableEntryIdentity(mountRootPath, path, RegularFileType, requireSingleLink: true);

    internal static void ValidateMutableDirectoryIdentity(string path, string expectedIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedIdentity);
        if (!string.Equals(GetMutableDirectoryIdentity(path), expectedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Runtime-owned directory '{path}' no longer has its expected physical identity.");
        }
    }

    internal static void ValidateMutableDirectoryIdentity(
        string mountRootPath,
        string path,
        string expectedIdentity)
        => ValidateMutableEntryIdentity(
            mountRootPath,
            path,
            expectedIdentity,
            DirectoryType,
            requireSingleLink: false);

    internal static void ValidateMutableRegularFileIdentity(
        string mountRootPath,
        string path,
        string expectedIdentity)
        => ValidateMutableEntryIdentity(
            mountRootPath,
            path,
            expectedIdentity,
            RegularFileType,
            requireSingleLink: true);

    internal static string GetCanonicalPhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
                   ?? throw new InvalidDataException($"Path '{path}' has no filesystem root.");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            if (!Path.Exists(candidate) && !IsReparsePoint(candidate))
            {
                current = candidate;
                continue;
            }
            FileSystemInfo entry = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : new FileInfo(candidate);
            current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
            if (OperatingSystem.IsWindows())
            {
                current = GetWindowsFinalPath(current);
            }
        }
        return Path.GetFullPath(current);
    }

    internal static void ValidateNoReparsePointAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (IsReparsePoint(current) && !IsTrustedMacOsSystemAlias(path, current))
            {
                throw new InvalidDataException(
                    $"Runtime-owned path '{path}' contains a symbolic link or reparse-point component: '{current}'.");
            }
        }
    }

    private static void ValidateMutableEntry(
        string mountRootPath,
        string path,
        uint expectedType,
        bool requireSingleLink)
    {
        var mountRoot = Inspect(mountRootPath);
        var entry = Inspect(path);
        if ((mountRoot.Mode & FileTypeMask) != DirectoryType
            || (entry.Mode & FileTypeMask) != expectedType)
        {
            throw new InvalidDataException(
                $"Runtime-owned path '{path}' is not the expected physical file type.");
        }
        if (mountRoot.MountIdentity != entry.MountIdentity)
        {
            throw new InvalidDataException(
                $"Runtime-owned path '{path}' crosses a filesystem mount boundary.");
        }
        if (OperatingSystem.IsWindows())
        {
            ValidateWindowsOwner(path, expectedType == DirectoryType);
        }
        else if (entry.OwnerUserId != getuid())
        {
            throw new InvalidDataException(
                $"Runtime-owned path '{path}' is not owned by the current user.");
        }
        if (requireSingleLink && entry.LinkCount != 1)
        {
            throw new InvalidDataException(
                $"Runtime-owned file '{path}' has aliases outside its unique deletion identity.");
        }
    }

    private static string GetMutableEntryIdentity(
        string mountRootPath,
        string path,
        uint expectedType,
        bool requireSingleLink)
    {
        ValidateMutableEntry(mountRootPath, path, expectedType, requireSingleLink);
        return Inspect(path).Identity;
    }

    private static void ValidateMutableEntryIdentity(
        string mountRootPath,
        string path,
        string expectedIdentity,
        uint expectedType,
        bool requireSingleLink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedIdentity);
        var actualIdentity = GetMutableEntryIdentity(
            mountRootPath,
            path,
            expectedType,
            requireSingleLink);
        if (!string.Equals(actualIdentity, expectedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Runtime-owned path '{path}' no longer has its expected physical identity.");
        }
    }

    private static PhysicalEntry Inspect(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            return InspectWindows(fullPath);
        }
        if (OperatingSystem.IsLinux())
        {
            return InspectLinux(fullPath);
        }
        if (OperatingSystem.IsMacOS())
        {
            return InspectMacOs(fullPath);
        }
        throw new PlatformNotSupportedException(
            "Runtime-owned physical path validation supports Windows, Linux, and macOS only.");
    }

    private readonly record struct PhysicalEntry(
        uint Mode,
        uint LinkCount,
        uint OwnerUserId,
        long MountIdentity,
        string Identity);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null
                   || new DirectoryInfo(path).LinkTarget is not null
                   || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

}
