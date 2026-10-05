using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Sunder.Runtime.Host.Services;

internal static class DurableJsonDocument
{
    public static async Task WriteAsync<T>(
        string path,
        T document,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, options) + Environment.NewLine);
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            MoveFile(temporaryPath, path, overwrite: true);
            try
            {
                using var committed = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                committed.Flush(flushToDisk: true);
                SyncDirectory(Path.GetDirectoryName(path)!);
            }
            catch (Exception exception)
            {
                throw new DurableWriteIndeterminateException(path, exception);
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // A uniquely named temporary document is safe for later garbage collection.
            }
        }
    }

    public static void Delete(string path)
    {
        File.Delete(path);
        SyncDirectory(Path.GetDirectoryName(path)!);
    }

    public static void CreateDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(fullPath);
        var current = fullPath;
        while (true)
        {
            SyncDirectory(current);
            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }
            current = parent;
        }
    }

    public static void SyncDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var descriptor = open(path, 0);
        if (descriptor < 0)
        {
            throw new IOException(
                $"Could not open durable document directory '{path}' for synchronization.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        try
        {
            if (fsync(descriptor) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is not (22 or 30 or 45 or 95))
                {
                    throw new IOException(
                        $"Could not synchronize durable document directory '{path}'.",
                        new Win32Exception(error));
                }
            }
        }
        finally
        {
            close(descriptor);
        }
    }

    public static void MoveDirectory(string sourcePath, string destinationPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.Move(sourcePath, destinationPath);
            return;
        }
        MoveWindows(sourcePath, destinationPath, replaceExisting: false);
    }

    private static void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(sourcePath, destinationPath, overwrite);
            return;
        }
        MoveWindows(sourcePath, destinationPath, overwrite);
    }

    private static void MoveWindows(string sourcePath, string destinationPath, bool replaceExisting)
    {
        const uint moveFileReplaceExisting = 0x1;
        const uint moveFileWriteThrough = 0x8;
        var flags = moveFileWriteThrough | (replaceExisting ? moveFileReplaceExisting : 0);
        if (!MoveFileEx(sourcePath, destinationPath, flags))
        {
            throw new IOException(
                $"Could not durably move '{sourcePath}' to '{destinationPath}'.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int descriptor);

    [DllImport("libc")]
    private static extern int close(int descriptor);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingPath, string newPath, uint flags);
}

internal sealed class DurableWriteIndeterminateException(string path, Exception innerException)
    : IOException(
        $"Durable replacement of '{path}' became visible, but persistence could not be confirmed.",
        innerException);
