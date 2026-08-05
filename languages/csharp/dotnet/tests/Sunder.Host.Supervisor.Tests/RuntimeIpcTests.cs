using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using Sunder.Host.Supervisor;
using Sunder.Runtime.Client;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Host.Supervisor.Tests;

public sealed partial class HostSupervisorFoundationTests
{
    [Fact]
    public void RuntimeWorkerEndpointLease_CreatesPrivatePlatformEndpointAndCleansUp()
    {
        string? directoryPath = null;
        string? socketPath = null;
        using (var endpointLease = RuntimeWorkerEndpointLease.Create())
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(RuntimeIpcTransportKind.NamedPipe, endpointLease.Endpoint.Kind);
                Assert.StartsWith("sunder-runtime-", endpointLease.Endpoint.Address, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(RuntimeIpcTransportKind.UnixSocket, endpointLease.Endpoint.Kind);
                socketPath = endpointLease.Endpoint.Address;
                directoryPath = Path.GetDirectoryName(socketPath);
                Assert.NotNull(directoryPath);
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(directoryPath));
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.False(File.Exists(socketPath));
            Assert.False(Directory.Exists(directoryPath));
        }
    }

    [Fact]
    public void RuntimeIpcEndpoint_RoundTripsAndRejectsInvalidAddresses()
    {
        var endpoint = OperatingSystem.IsWindows()
            ? new RuntimeIpcEndpoint(RuntimeIpcTransportKind.NamedPipe, "sunder-runtime-test")
            : new RuntimeIpcEndpoint(RuntimeIpcTransportKind.UnixSocket, "/tmp/sunder-runtime-test.sock");

        Assert.Equal(endpoint, RuntimeIpcEndpoint.Parse(endpoint.ToEnvironmentValue()));
        endpoint.EnsureSupportedPlatform();
        Assert.Throws<FormatException>(() => RuntimeIpcEndpoint.Parse("tcp:127.0.0.1:1234"));
        Assert.Throws<ArgumentException>(() =>
            new RuntimeIpcEndpoint(RuntimeIpcTransportKind.NamedPipe, "folder/pipe"));
        Assert.Throws<ArgumentException>(() =>
            new RuntimeIpcEndpoint(RuntimeIpcTransportKind.UnixSocket, "relative.sock"));

        using var handler = Assert.IsType<SocketsHttpHandler>(
            RuntimeIpcHttpMessageHandlerFactory.Create(endpoint));
        Assert.False(handler.UseProxy);
        Assert.Equal(TimeSpan.FromSeconds(5), handler.ConnectTimeout);
    }

    [Fact]
    public async Task RuntimeIpcHttpMessageHandler_ConnectsToLogicalRuntimeOrigin()
    {
        using var endpointLease = RuntimeWorkerEndpointLease.Create();
        IDisposable listener;
        Task server;
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeServerStream(
                endpointLease.Endpoint.Address,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            listener = pipe;
            server = Task.Run(async () =>
            {
                await pipe.WaitForConnectionAsync();
                await ServeSingleHttpRequestAsync(pipe);
            });
        }
        else
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(endpointLease.Endpoint.Address));
            socket.Listen(1);
            listener = socket;
            server = Task.Run(async () =>
            {
                using var accepted = await socket.AcceptAsync();
                await using var stream = new NetworkStream(accepted, ownsSocket: false);
                await ServeSingleHttpRequestAsync(stream);
            });
        }

        using (listener)
        using (var client = new HttpClient(RuntimeIpcHttpMessageHandlerFactory.Create(endpointLease.Endpoint))
        {
            Timeout = TimeSpan.FromSeconds(5),
        })
        {
            Assert.Equal("ok", await client.GetStringAsync(RuntimeIpcEndpoint.LogicalRuntimeUrl));
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task ServeSingleHttpRequestAsync(Stream stream)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
        {
        }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
        await stream.FlushAsync();
    }
}
