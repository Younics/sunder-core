using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Host.Services;

internal static class ExecutionRuntimeRootFileSystem
{
    private const UnixFileMode SealedFileMode =
        UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode SealedLauncherMode = SealedFileMode | UnixFileMode.UserExecute;
    private const UnixFileMode SealedDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode WritableFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode WritableDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal static void PrepareForPublication(string rootPath, string launcherPath)
        => SealCore(rootPath, launcherPath, sealRoot: false);

    internal static void Seal(string rootPath, string launcherPath)
        => SealCore(rootPath, launcherPath, sealRoot: true);

    internal static void VerifyPreparedForPublication(string rootPath, string launcherPath)
    {
        var tree = InspectTree(rootPath, rejectLinks: true);
        VerifyTreeSealed(tree, EnsureLauncher(tree, launcherPath), includeRoot: false);
    }

    private static void SealCore(string rootPath, string launcherPath, bool sealRoot)
    {
        var tree = InspectTree(rootPath, rejectLinks: true);
        var launcher = EnsureLauncher(tree, launcherPath);
        if (!sealRoot)
        {
            MakeDirectoryWritable(tree.RootPath);
        }

        foreach (var file in tree.Files)
        {
            EnsureExpectedType(file, directory: false);
            RuntimeOwnedFileSystem.ValidateMutableRegularFile(tree.RootPath, file);
            if (OperatingSystem.IsWindows())
            {
                SealWindowsPath(file, directory: false);
            }
            else
            {
                File.SetUnixFileMode(file, PathsEqual(file, launcher) ? SealedLauncherMode : SealedFileMode);
            }
        }

        foreach (var directory in tree.Directories
                     .Where(path => sealRoot || !PathsEqual(path, tree.RootPath))
                     .OrderByDescending(static path => path.Length))
        {
            RuntimeOwnedFileSystem.ValidateMutableDirectory(tree.RootPath, directory);
            SealRootDirectory(directory);
        }

        VerifyTreeSealed(tree, launcher, sealRoot);
    }

    internal static void VerifySealed(string rootPath, string launcherPath)
    {
        var tree = InspectTree(rootPath, rejectLinks: true);
        VerifyTreeSealed(tree, EnsureLauncher(tree, launcherPath), includeRoot: true);
    }

    private static void VerifyTreeSealed(TreeSnapshot tree, string launcher, bool includeRoot)
    {
        foreach (var file in tree.Files)
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(tree.RootPath, file));
            if (OperatingSystem.IsWindows())
            {
                if ((File.GetAttributes(file) & FileAttributes.ReadOnly) == 0
                    || !HasExpectedWindowsAccess(file, directory: false))
                {
                    throw PermissionDrift("file", relativePath);
                }
            }
            else
            {
                var expected = PathsEqual(file, launcher) ? SealedLauncherMode : SealedFileMode;
                if (File.GetUnixFileMode(file) != expected)
                {
                    throw PermissionDrift("file", relativePath);
                }
            }
        }

        foreach (var directory in tree.Directories.Where(path => includeRoot || !PathsEqual(path, tree.RootPath)))
        {
            var relativePath = PathsEqual(directory, tree.RootPath)
                ? "."
                : NormalizeRelativePath(Path.GetRelativePath(tree.RootPath, directory));
            if (OperatingSystem.IsWindows())
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReadOnly) == 0
                    || !HasExpectedWindowsAccess(directory, directory: true))
                {
                    throw PermissionDrift("directory", relativePath);
                }
            }
            else if (File.GetUnixFileMode(directory) != SealedDirectoryMode)
            {
                throw PermissionDrift("directory", relativePath);
            }
        }
    }

    internal static void MakeOwnedTreeWritable(string rootPath)
        => MakeTreeWritable(InspectTree(rootPath, rejectLinks: false));

    internal static void MakeOwnedRootMovable(string rootPath)
    {
        var path = Path.GetFullPath(rootPath);
        RuntimeOwnedFileSystem.ValidateMutableDirectory(Path.GetDirectoryName(path) ?? path, path);
        MakeDirectoryWritable(path);
    }

    internal static void SealRootDirectory(string rootPath)
    {
        var path = Path.GetFullPath(rootPath);
        EnsureExpectedType(path, directory: true);
        RuntimeOwnedFileSystem.ValidateMutableDirectory(Path.GetDirectoryName(path) ?? path, path);
        if (OperatingSystem.IsWindows())
        {
            SealWindowsPath(path, directory: true);
        }
        else
        {
            File.SetUnixFileMode(path, SealedDirectoryMode);
        }
    }

    internal static bool TryDeleteOwnedTree(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return true;
        }
        try
        {
            DeleteOwnedTree(rootPath);
            return !EntryExists(rootPath);
        }
        catch
        {
            return false;
        }
    }

    internal static void DeleteOwnedTree(string rootPath)
    {
        var fullRootPath = Path.GetFullPath(rootPath);
        if (!TryGetAttributes(fullRootPath, out var rootAttributes))
        {
            return;
        }
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            DeleteLink(fullRootPath, rootAttributes);
            return;
        }
        if ((rootAttributes & FileAttributes.Directory) == 0)
        {
            throw new IOException($"Execution runtime owned tree '{rootPath}' is not a directory.");
        }

        var tree = InspectTree(fullRootPath, rejectLinks: false);
        MakeTreeWritable(tree);
        foreach (var file in tree.Files)
        {
            DeleteRegularFile(file);
        }
        foreach (var link in tree.Links)
        {
            if (TryGetAttributes(link.Path, out var attributes))
            {
                DeleteLink(link.Path, attributes);
            }
        }
        foreach (var directory in tree.Directories
                     .Where(path => !PathsEqual(path, tree.RootPath))
                     .OrderByDescending(static path => path.Length))
        {
            DeleteDirectory(directory);
        }
        DeleteDirectory(tree.RootPath);
    }

    private static TreeSnapshot InspectTree(string rootPath, bool rejectLinks)
    {
        var fullRootPath = Path.GetFullPath(rootPath);
        EnsureExpectedType(fullRootPath, directory: true);
        RuntimeOwnedFileSystem.ValidateMutableDirectory(
            Path.GetDirectoryName(fullRootPath) ?? fullRootPath,
            fullRootPath);
        var files = new List<string>();
        var directories = new List<string> { fullRootPath };
        var links = new List<LinkEntry>();
        var pending = new Stack<string>();
        pending.Push(fullRootPath);
        while (pending.TryPop(out var currentPath))
        {
            EnsureExpectedType(currentPath, directory: true);
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(
                         currentPath,
                         "*",
                         SearchOption.TopDirectoryOnly).ToArray())
            {
                var fullEntryPath = EnsureContained(entryPath, fullRootPath);
                var attributes = File.GetAttributes(fullEntryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (rejectLinks)
                    {
                        throw new InvalidDataException(
                            $"Execution runtime root path '{NormalizeRelativePath(Path.GetRelativePath(fullRootPath, fullEntryPath))}' must not be a link or reparse point.");
                    }
                    links.Add(new LinkEntry(fullEntryPath));
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    RuntimeOwnedFileSystem.ValidateMutableDirectory(fullRootPath, fullEntryPath);
                    directories.Add(fullEntryPath);
                    pending.Push(fullEntryPath);
                    continue;
                }

                PackageStoreFileSystem.EnsureRegularFile(fullEntryPath);
                RuntimeOwnedFileSystem.ValidateMutableRegularFile(fullRootPath, fullEntryPath);
                files.Add(fullEntryPath);
            }
        }
        return new TreeSnapshot(fullRootPath, files, directories, links);
    }

    private static void MakeTreeWritable(TreeSnapshot tree)
    {
        foreach (var directory in tree.Directories.OrderBy(static path => path.Length))
        {
            RuntimeOwnedFileSystem.ValidateMutableDirectory(tree.RootPath, directory);
            MakeDirectoryWritable(directory);
        }
        foreach (var file in tree.Files)
        {
            EnsureExpectedType(file, directory: false);
            RuntimeOwnedFileSystem.ValidateMutableRegularFile(tree.RootPath, file);
            if (OperatingSystem.IsWindows())
            {
                SetWindowsAccess(file, directory: false, FileSystemRights.FullControl);
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }
            else
            {
                File.SetUnixFileMode(file, WritableFileMode);
            }
        }
    }

    private static void MakeDirectoryWritable(string path)
    {
        EnsureExpectedType(path, directory: true);
        if (OperatingSystem.IsWindows())
        {
            SetWindowsAccess(path, directory: true, FileSystemRights.FullControl);
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(path, WritableDirectoryMode);
        }
    }

    private static void EnsureExpectedType(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || ((attributes & FileAttributes.Directory) != 0) != directory)
        {
            throw new InvalidDataException(
                $"Execution runtime root path '{path}' changed type or became a link.");
        }
        if (!directory)
        {
            PackageStoreFileSystem.EnsureRegularFile(path);
        }
    }

    private static void DeleteRegularFile(string path)
    {
        if (!TryGetAttributes(path, out var attributes))
        {
            return;
        }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            DeleteLink(path, attributes);
            return;
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new IOException($"Execution runtime owned file '{path}' changed into a directory.");
        }
        PackageStoreFileSystem.EnsureRegularFile(path);
        File.Delete(path);
    }

    private static void DeleteDirectory(string path)
    {
        if (!TryGetAttributes(path, out var attributes))
        {
            return;
        }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            DeleteLink(path, attributes);
            return;
        }
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException($"Execution runtime owned directory '{path}' changed into a file.");
        }
        Directory.Delete(path, recursive: false);
    }

    private static void DeleteLink(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Directory) != 0)
        {
            Directory.Delete(path, recursive: false);
        }
        else
        {
            File.Delete(path);
        }
    }

    private static string EnsureContained(string path, string rootPath)
    {
        var root = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(path);
        if (!string.Equals(candidate, root, PathComparison)
            && !candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidDataException(
                $"Execution runtime root path '{path}' is outside its owned tree.");
        }
        return candidate;
    }

    private static string EnsureLauncher(TreeSnapshot tree, string launcherPath)
    {
        var launcher = EnsureContained(launcherPath, tree.RootPath);
        if (!tree.Files.Contains(launcher, PathComparer))
        {
            throw new InvalidDataException("The execution runtime launcher is not a regular file in its root.");
        }
        return launcher;
    }

    [SupportedOSPlatform("windows")]
    private static void SealWindowsPath(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) == 0)
        {
            SetWindowsAccess(path, directory, FileSystemRights.FullControl);
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
        }
        SetWindowsAccess(path, directory, WindowsSealedRights);
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemRights WindowsSealedRights =>
        FileSystemRights.ReadAndExecute | FileSystemRights.ChangePermissions;

    [SupportedOSPlatform("windows")]
    private static FileSystemRights WindowsMutationRights =>
        FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.TakeOwnership;

    [SupportedOSPlatform("windows")]
    private static void SetWindowsAccess(string path, bool directory, FileSystemRights rights)
    {
        var identity = WindowsIdentity.GetCurrent().User
                       ?? throw new IOException("The current Windows user has no security identifier.");
        FileSystemSecurity current = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
        if (!identity.Equals(current.GetOwner(typeof(SecurityIdentifier))))
        {
            throw new InvalidDataException(
                $"Execution runtime path '{path}' is not owned by the current Windows user.");
        }
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            rights,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        if (directory)
        {
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        }
        else
        {
            new FileInfo(path).SetAccessControl((FileSecurity)security);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasExpectedWindowsAccess(string path, bool directory)
    {
        var identity = WindowsIdentity.GetCurrent().User
                       ?? throw new IOException("The current Windows user has no security identifier.");
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!identity.Equals(security.GetOwner(typeof(SecurityIdentifier))) || !security.AreAccessRulesProtected)
        {
            return false;
        }
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        return rules is
        [
            {
                AccessControlType: AccessControlType.Allow,
                IsInherited: false,
                InheritanceFlags: InheritanceFlags.None,
                PropagationFlags: PropagationFlags.None,
            } rule,
        ]
        && identity.Equals(rule.IdentityReference)
        && (rule.FileSystemRights & WindowsSealedRights) == WindowsSealedRights
        && (rule.FileSystemRights & WindowsMutationRights) == 0;
    }

    private static bool EntryExists(string path)
        => TryGetAttributes(Path.GetFullPath(path), out _);

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static InvalidDataException PermissionDrift(string kind, string relativePath)
        => new($"Published execution runtime {kind} '{relativePath}' has unsafe permission drift.");

    private static string NormalizeRelativePath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static bool PathsEqual(string first, string second)
        => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), PathComparison);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record TreeSnapshot(
        string RootPath,
        IReadOnlyList<string> Files,
        IReadOnlyList<string> Directories,
        IReadOnlyList<LinkEntry> Links);

    private sealed record LinkEntry(string Path);
}
