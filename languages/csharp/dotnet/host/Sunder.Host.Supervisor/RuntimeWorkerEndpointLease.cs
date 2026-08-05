using System.Security.Cryptography;
using Sunder.Runtime.LocalState;

namespace Sunder.Host.Supervisor;

internal sealed class RuntimeWorkerEndpointLease : IDisposable
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private readonly string? _directoryPath;
    private int _disposed;

    private RuntimeWorkerEndpointLease(RuntimeIpcEndpoint endpoint, string? directoryPath)
    {
        Endpoint = endpoint;
        _directoryPath = directoryPath;
    }

    public RuntimeIpcEndpoint Endpoint { get; }

    public static RuntimeWorkerEndpointLease Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new RuntimeWorkerEndpointLease(
                new RuntimeIpcEndpoint(
                    RuntimeIpcTransportKind.NamedPipe,
                    $"sunder-runtime-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}"),
                directoryPath: null);
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var directoryPath = Path.Combine(
                "/tmp",
                $"sunder-runtime-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}");
            if (Directory.Exists(directoryPath) || File.Exists(directoryPath))
            {
                continue;
            }
            try
            {
                Directory.CreateDirectory(directoryPath, PrivateDirectoryMode);
                File.SetUnixFileMode(directoryPath, PrivateDirectoryMode);
                return new RuntimeWorkerEndpointLease(
                    new RuntimeIpcEndpoint(
                        RuntimeIpcTransportKind.UnixSocket,
                        Path.Combine(directoryPath, "worker.sock")),
                    directoryPath);
            }
            catch (IOException) when (Directory.Exists(directoryPath) || File.Exists(directoryPath))
            {
            }
        }
        throw new IOException("Could not create a private Runtime IPC directory.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || _directoryPath is null)
        {
            return;
        }
        try
        {
            if (File.Exists(Endpoint.Address))
            {
                File.Delete(Endpoint.Address);
            }
            if (Directory.Exists(_directoryPath))
            {
                Directory.Delete(_directoryPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
