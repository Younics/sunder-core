using Sunder.Package.Projections;

namespace Sunder.App.Services;

// Draining package work permits activation retirement. It does not prove that
// the CLR has released the package's mapped files. Keep their storage ownership
// until the collectible context dies; durable leases also survive an App crash
// and are recovered through the existing process-identity checks.
internal static class AppPackageFileReclamation
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim ReclamationGate = new(1);
    private static readonly List<PendingFiles> Pending = [];
    private static Task? _worker;

    internal static void Defer(AppPackageLoadContext context, PackageProjectionAcquisition? projection, string? folder)
    {
        if (projection is null && folder is null) return;
        var pending = new PendingFiles([new WeakReference(context, trackResurrection: true)], projection, folder);
        lock (Gate)
        {
            Pending.Add(pending);
            if (_worker is not null) return;
            // Contextual reflection and package cleanup authority can retain the
            // very ALC whose collection this worker awaits.
            if (ExecutionContext.IsFlowSuppressed()) _worker = Task.Run(ReclaimLoopAsync);
            else { using (ExecutionContext.SuppressFlow()) _worker = Task.Run(ReclaimLoopAsync); }
        }
    }

    internal static void ReleaseDirectory(string folder)
    {
        lock (Gate)
        {
            // A generation can contain several independently retired packages.
            // Its last activation must not recursively delete a still-loaded child.
            var contexts = Pending.Where(item => item.Folder is not null && IsWithin(folder, item.Folder))
                .SelectMany(item => item.Contexts).Distinct().ToArray();
            if (contexts.Length != 0)
            {
                Pending.Add(new PendingFiles(contexts, null, folder));
                return;
            }
        }
        AppPackageSourcePreparer.TryDeleteDirectory(folder);
    }

    private static bool IsWithin(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static async Task ReclaimAsync()
    {
        await ReclamationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            PendingFiles[] ready;
            lock (Gate)
            {
                ready = Pending.Where(item => item.Contexts.All(context => !context.IsAlive)).ToArray();
            }
            foreach (var item in ready)
            {
                try
                {
                    if (item.Projection is not null) await item.Projection.DisposeAsync().ConfigureAwait(false);
                    else if (item.Folder is not null) AppPackageSourcePreparer.TryDeleteDirectory(item.Folder);
                }
                catch (Exception error) when (AppPackageUiFaultPolicy.IsRecoverable(error))
                {
                    AppCleanupQuarantine.Retain(item, "releasing collected App package files");
                    AppSessionLog.WriteError("Failed to release collected App package files; storage ownership is retained for process-exit recovery.", error);
                }
                finally { lock (Gate) Pending.Remove(item); }
            }
        }
        finally { ReclamationGate.Release(); }
    }

    private static async Task ReclaimLoopAsync()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            await ReclaimAsync().ConfigureAwait(false);
            lock (Gate)
            {
                if (Pending.Count != 0) continue;
                _worker = null;
                return;
            }
        }
    }

    private sealed record PendingFiles(WeakReference[] Contexts, PackageProjectionAcquisition? Projection, string? Folder);
}
