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

            File.Move(temporaryPath, path, overwrite: true);
            using var committed = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            committed.Flush(flushToDisk: true);
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
}
