using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Sunder.Runtime.Client;

public static class RuntimeConnectionInfoStore
{
    private const int FormatVersion = 1;
    private static readonly byte[] WindowsEntropy = "Sunder.Runtime.Connection.V1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static string GetDefaultPath()
        => Path.Combine(RuntimeLocalState.GetV1RootPath(), "connection.json");

    public static RuntimeConnectionInfo? Load(string? path = null)
    {
        var useDefaultPath = path is null || IsDefaultPath(path);
        path ??= GetDefaultPath();
        if (useDefaultPath)
        {
            RuntimeLocalState.EnsureInitialized();
        }
        if (!File.Exists(path))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows() && !HasPrivateUnixPermissions(path))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<ConnectionDocument>(File.ReadAllText(path), JsonOptions);
            if (document is null || document.Version != FormatVersion || string.IsNullOrWhiteSpace(document.RuntimeUrl))
            {
                return null;
            }

            var token = UnprotectToken(document);
            return string.IsNullOrWhiteSpace(token)
                || !Uri.TryCreate(document.RuntimeUrl, UriKind.Absolute, out var runtimeUrl)
                    ? null
                    : new RuntimeConnectionInfo(RuntimeConnectionInfo.Normalize(runtimeUrl), token);
        }
        catch
        {
            return null;
        }
    }

    public static RuntimeConnectionInfo LoadFor(Uri runtimeUrl, string? path = null)
    {
        var connection = Load(path);
        if (connection is null || !connection.Matches(runtimeUrl))
        {
            throw new InvalidOperationException(
                "No authenticated Runtime connection information is available for the configured Runtime URL. Start the Runtime through Sunder App or use the URL published by the running Runtime.");
        }

        return connection;
    }

    public static void Save(RuntimeConnectionInfo connection, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(connection.BearerToken))
        {
            throw new ArgumentException("Runtime bearer token is required.", nameof(connection));
        }

        var useDefaultPath = path is null || IsDefaultPath(path);
        path ??= GetDefaultPath();
        if (useDefaultPath)
        {
            RuntimeLocalState.EnsureInitialized();
        }
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Connection information path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, PrivateDirectoryMode);
        }

        var document = ProtectToken(connection);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
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

            using (var stream = new FileStream(tempPath, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(fullPath, PrivateFileMode);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public static void DeleteIfMatches(RuntimeConnectionInfo connection, string? path = null)
    {
        path ??= GetDefaultPath();
        var stored = Load(path);
        if (stored is null
            || !stored.Matches(connection.RuntimeUrl)
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(stored.BearerToken),
                System.Text.Encoding.UTF8.GetBytes(connection.BearerToken)))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // A newer process may be replacing the connection file concurrently.
        }
    }

    private static ConnectionDocument ProtectToken(RuntimeConnectionInfo connection)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ConnectionDocument(FormatVersion, RuntimeConnectionInfo.Normalize(connection.RuntimeUrl).ToString(), connection.BearerToken, null);
        }

        var protectedToken = ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(connection.BearerToken),
            WindowsEntropy,
            DataProtectionScope.CurrentUser);
        return new ConnectionDocument(
            FormatVersion,
            RuntimeConnectionInfo.Normalize(connection.RuntimeUrl).ToString(),
            null,
            Convert.ToBase64String(protectedToken));
    }

    private static string? UnprotectToken(ConnectionDocument document)
    {
        if (!OperatingSystem.IsWindows())
        {
            return document.Token;
        }

        if (string.IsNullOrWhiteSpace(document.ProtectedToken))
        {
            return null;
        }

        var token = ProtectedData.Unprotect(
            Convert.FromBase64String(document.ProtectedToken),
            WindowsEntropy,
            DataProtectionScope.CurrentUser);
        return System.Text.Encoding.UTF8.GetString(token);
    }

    private static bool IsDefaultPath(string path)
        => string.Equals(
            Path.GetFullPath(path),
            Path.GetFullPath(GetDefaultPath()),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [UnsupportedOSPlatform("windows")]
    private static bool HasPrivateUnixPermissions(string path)
    {
        const UnixFileMode nonOwnerPermissions =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        return directory is not null
               && (File.GetUnixFileMode(directory) & nonOwnerPermissions) == 0
               && (File.GetUnixFileMode(path) & nonOwnerPermissions) == 0;
    }

    private sealed record ConnectionDocument(int Version, string RuntimeUrl, string? Token, string? ProtectedToken);
}
