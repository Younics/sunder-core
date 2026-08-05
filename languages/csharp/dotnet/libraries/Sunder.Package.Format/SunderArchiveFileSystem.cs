namespace Sunder.Package.Format;

internal static class SunderArchiveFileSystem
{
    public static IReadOnlyList<(ArchiveRelativePath Path, string FullPath)> EnumerateFiles(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        EnsureNotLink(root);
        var files = new List<(ArchiveRelativePath Path, string FullPath)>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(directory))
            {
                EnsureNotLink(entryPath);
                var attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entryPath);
                    continue;
                }

                var relative = Path.GetRelativePath(root, entryPath).Replace(Path.DirectorySeparatorChar, '/');
                files.Add((ArchiveRelativePath.Parse(relative), entryPath));
            }
        }

        return files;
    }

    public static IReadOnlyList<ArchiveRelativePath> EnumerateDirectories(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        EnsureNotLink(root);
        var directories = new List<ArchiveRelativePath>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entryPath in Directory.EnumerateDirectories(directory))
            {
                EnsureNotLink(entryPath);
                var relative = Path.GetRelativePath(root, entryPath).Replace(Path.DirectorySeparatorChar, '/');
                directories.Add(ArchiveRelativePath.Parse(relative));
                pending.Push(entryPath);
            }
        }

        return directories;
    }

    public static string ResolveFile(string rootPath, ArchiveRelativePath relativePath)
    {
        var root = Path.GetFullPath(rootPath);
        EnsureNotLink(root);
        var current = root;
        foreach (var segment in relativePath.ToString().Split('/'))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
            {
                EnsureNotLink(current);
            }
        }

        return current;
    }

    public static void EnsureNotLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Path '{path}' is a symbolic link or reparse point.");
        }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // The caller still receives the archive failure; cleanup is best effort.
        }
    }

    public static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The caller still receives the archive failure; cleanup is best effort.
        }
    }
}
