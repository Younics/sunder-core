namespace Sunder.Runtime.LocalState;

public enum RuntimeIpcTransportKind
{
    UnixSocket = 1,
    NamedPipe = 2,
}

public sealed record RuntimeIpcEndpoint
{
    public const string EnvironmentVariable = "SUNDER_RUNTIME_SUPERVISOR_ENDPOINT";
    public static Uri LogicalRuntimeUrl { get; } = new("http://sunder-runtime/");

    public RuntimeIpcEndpoint(RuntimeIpcTransportKind kind, string address)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (string.IsNullOrWhiteSpace(address) || address.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Runtime IPC address is required.", nameof(address));
        }

        Kind = kind;
        Address = kind switch
        {
            RuntimeIpcTransportKind.UnixSocket when Path.IsPathFullyQualified(address) => Path.GetFullPath(address),
            RuntimeIpcTransportKind.UnixSocket => throw new ArgumentException(
                "Runtime Unix socket path must be absolute.",
                nameof(address)),
            RuntimeIpcTransportKind.NamedPipe when address.Length <= 200
                                                   && address.IndexOfAny(['/', '\\', ':']) < 0 => address,
            RuntimeIpcTransportKind.NamedPipe => throw new ArgumentException(
                "Runtime named pipe must be a simple name no longer than 200 characters.",
                nameof(address)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    public RuntimeIpcTransportKind Kind { get; }

    public string Address { get; }

    public string ToEnvironmentValue()
        => Kind switch
        {
            RuntimeIpcTransportKind.UnixSocket => $"unix:{Address}",
            RuntimeIpcTransportKind.NamedPipe => $"pipe:{Address}",
            _ => throw new InvalidOperationException("Unsupported Runtime IPC transport."),
        };

    public static RuntimeIpcEndpoint Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Runtime IPC endpoint is required.", nameof(value));
        }
        if (value.StartsWith("unix:", StringComparison.Ordinal))
        {
            return new RuntimeIpcEndpoint(RuntimeIpcTransportKind.UnixSocket, value[5..]);
        }
        if (value.StartsWith("pipe:", StringComparison.Ordinal))
        {
            return new RuntimeIpcEndpoint(RuntimeIpcTransportKind.NamedPipe, value[5..]);
        }
        throw new FormatException("Runtime IPC endpoint must use the 'unix:' or 'pipe:' transport prefix.");
    }

    public void EnsureSupportedPlatform()
    {
        if (Kind == RuntimeIpcTransportKind.NamedPipe && !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Runtime named-pipe transport is supported only on Windows.");
        }
        if (Kind == RuntimeIpcTransportKind.UnixSocket && OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Runtime Unix-socket transport is not supported on Windows.");
        }
    }
}
