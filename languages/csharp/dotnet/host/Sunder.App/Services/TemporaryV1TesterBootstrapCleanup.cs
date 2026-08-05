using System.Diagnostics;

namespace Sunder.App.Services;

// TEMPORARY V1 TESTER BOOTSTRAP CLEANUP
//
// Early V1 development builds created App-owned files before schema.json existed. This wrapper
// recognizes only that exact App V1 layout, replaces it atomically, and then runs the normal strict
// initializer. Remove this file after all pre-schema tester builds have launched a schema-aware
// build, and replace AppLocalState.EnsureInitializedForStartup() with AppLocalState.EnsureInitialized().
internal static class TemporaryV1TesterBootstrapCleanup
{
    private static readonly HashSet<string> KnownPreSchemaEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store",
        "cache",
        "logs",
        "notifications.json",
        "package-workspaces",
        "shell-state.json",
        "stacks",
        "update-settings.json",
    };

    internal static void EnsureInitialized(string rootPath, Action<string?> initialize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(initialize);

        var root = Path.GetFullPath(rootPath);
        var parent = Path.GetDirectoryName(root)
            ?? throw new InvalidOperationException("The App V1 state root must have a parent directory.");
        Directory.CreateDirectory(parent);

        using var bootstrapLock = AcquireBootstrapLock(parent);
        if (!Directory.Exists(root) || File.Exists(Path.Combine(root, "schema.json")))
        {
            initialize(root);
            return;
        }

        var entries = Directory.EnumerateFileSystemEntries(root).ToArray();
        var contentEntries = entries
            .Where(path => !Path.GetFileName(path).StartsWith(".schema.json.", StringComparison.Ordinal))
            .ToArray();
        if (contentEntries.Length == 0)
        {
            initialize(root);
            return;
        }

        ValidateKnownPreSchemaLayout(root, contentEntries);

        var quarantinePath = Path.Combine(parent, $".{Path.GetFileName(root)}.pre-schema-cleanup.{Guid.NewGuid():N}");
        Directory.Move(root, quarantinePath);
        try
        {
            initialize(root);
        }
        catch
        {
            TryDeleteDirectory(root);
            Directory.Move(quarantinePath, root);
            throw;
        }

        if (!TryDeleteDirectory(quarantinePath))
        {
            Trace.WriteLine("Sunder initialized fresh App V1 state, but could not remove the quarantined pre-schema tester state.");
        }
    }

    private static FileStream AcquireBootstrapLock(string parent)
    {
        var lockPath = Path.Combine(parent, ".v1-bootstrap-cleanup.lock");
        var timeoutAt = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException) when (DateTime.UtcNow < timeoutAt)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void ValidateKnownPreSchemaLayout(string root, IReadOnlyList<string> entries)
    {
        var rootInfo = new DirectoryInfo(root);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw IncompatibleState();
        }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            var attributes = File.GetAttributes(entry);
            if (!KnownPreSchemaEntries.Contains(name)
                || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw IncompatibleState();
            }
        }

        ValidateNoNestedReparsePoints(root);
    }

    private static void ValidateNoNestedReparsePoints(string root)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        while (pendingDirectories.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw IncompatibleState();
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(entry);
                }
            }
        }
    }

    private static InvalidDataException IncompatibleState()
        => new(
            "The App V1 state root has no schema metadata and does not match the temporary pre-schema tester layout. "
            + "The state was left unchanged.");

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
