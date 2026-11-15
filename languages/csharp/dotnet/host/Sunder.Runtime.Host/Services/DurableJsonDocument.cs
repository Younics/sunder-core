using System.Text;
using System.Text.Json;
using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Host.Services;

internal static partial class DurableJsonDocument
{
    public static async Task WriteAsync<T>(
        string path,
        T document,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        RuntimeOwnedFileSystem.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, options) + Environment.NewLine);
            await using (var stream = RuntimeOwnedFileSystem.CreateFile(
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
        RuntimeOwnedFileSystem.CreateDirectory(fullPath);
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
        SyncUnixDirectory(path);
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

}

internal sealed class DurableWriteIndeterminateException(string path, Exception innerException)
    : IOException(
        $"Durable replacement of '{path}' became visible, but persistence could not be confirmed.",
        innerException);
