using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Host;

internal static class RuntimeSupervisedTransport
{
    private const UnixFileMode PrivateSocketMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static RuntimeIpcEndpoint? ReadFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(RuntimeIpcEndpoint.EnvironmentVariable);
        Environment.SetEnvironmentVariable(RuntimeIpcEndpoint.EnvironmentVariable, null);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var endpoint = RuntimeIpcEndpoint.Parse(value);
        endpoint.EnsureSupportedPlatform();
        return endpoint;
    }

    public static void Configure(IWebHostBuilder webHost, RuntimeIpcEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(webHost);
        ArgumentNullException.ThrowIfNull(endpoint);
        endpoint.EnsureSupportedPlatform();
        if (OperatingSystem.IsWindows())
        {
            if (endpoint.Kind != RuntimeIpcTransportKind.NamedPipe)
            {
                throw new PlatformNotSupportedException("Windows supervised Runtime requires a named pipe.");
            }
            webHost.UseNamedPipes(options => options.CurrentUserOnly = true);
            webHost.ConfigureKestrel(options =>
                options.ListenNamedPipe(
                    endpoint.Address,
                    listen => listen.Protocols = HttpProtocols.Http1));
            return;
        }

        if (endpoint.Kind != RuntimeIpcTransportKind.UnixSocket)
        {
            throw new PlatformNotSupportedException("Unix supervised Runtime requires a Unix socket.");
        }

        webHost.ConfigureKestrel(options =>
            options.ListenUnixSocket(
                endpoint.Address,
                listen => listen.Protocols = HttpProtocols.Http1));
    }

    public static void RestrictEndpoint(RuntimeIpcEndpoint endpoint)
    {
        if (OperatingSystem.IsWindows() || endpoint.Kind != RuntimeIpcTransportKind.UnixSocket)
        {
            return;
        }
        if (!File.Exists(endpoint.Address))
        {
            throw new IOException("Kestrel did not create the supervised Runtime Unix socket.");
        }
        File.SetUnixFileMode(endpoint.Address, PrivateSocketMode);
    }

    public static void CleanupEndpoint(RuntimeIpcEndpoint? endpoint)
    {
        if (endpoint is null
            || OperatingSystem.IsWindows()
            || endpoint.Kind != RuntimeIpcTransportKind.UnixSocket)
        {
            return;
        }
        var directory = Path.GetDirectoryName(endpoint.Address);
        if (directory is null
            || !string.Equals(Path.GetFileName(endpoint.Address), "worker.sock", StringComparison.Ordinal)
            || !Path.GetFileName(directory).StartsWith("sunder-runtime-", StringComparison.Ordinal)
            || !string.Equals(Path.GetDirectoryName(directory), "/tmp", StringComparison.Ordinal))
        {
            return;
        }
        try
        {
            if (File.Exists(endpoint.Address))
            {
                File.Delete(endpoint.Address);
            }
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
