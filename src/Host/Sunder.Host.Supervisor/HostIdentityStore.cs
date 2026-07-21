using System.Text.Json;

namespace Sunder.Host.Supervisor;

internal sealed class HostIdentityStore(string rootPath)
{
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _rootPath = Path.GetFullPath(rootPath);

    public static string GetDefaultRootPath()
    {
        var configured = Environment.GetEnvironmentVariable("SUNDER_HOST_STATE_ROOT");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sunder", "host", "v1")
            : Path.GetFullPath(configured);
    }

    public Guid LoadOrCreateHostId()
    {
        Directory.CreateDirectory(_rootPath);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_rootPath, PrivateDirectoryMode);
        }

        var path = Path.Combine(_rootPath, "identity.json");
        try
        {
            if (File.Exists(path))
            {
                var existing = JsonSerializer.Deserialize<HostIdentityDocument>(File.ReadAllText(path), JsonOptions);
                if (existing is { Version: 1, HostId: not null }
                    && Guid.TryParse(existing.HostId, out var hostId)
                    && hostId != Guid.Empty)
                {
                    return hostId;
                }
                throw new InvalidDataException("Sunder Host identity is invalid.");
            }

            var created = Guid.NewGuid();
            var tempPath = Path.Combine(_rootPath, $".identity.{Guid.NewGuid():N}.tmp");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = PrivateFileMode;
            }
            try
            {
                using (var stream = new FileStream(tempPath, options))
                {
                    JsonSerializer.Serialize(stream, new HostIdentityDocument(1, created.ToString("D")), JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(tempPath, path, overwrite: false);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, PrivateFileMode);
                }
                return created;
            }
            catch (IOException) when (File.Exists(path))
            {
                return LoadOrCreateHostId();
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Sunder Host identity is invalid.", exception);
        }
    }

    private sealed record HostIdentityDocument(int Version, string? HostId);
}
