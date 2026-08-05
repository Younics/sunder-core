using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunder.App.Services;

internal sealed class AppSingleInstanceCoordinator : IDisposable
{
    private static readonly TimeSpan DefaultForwardTimeout = TimeSpan.FromSeconds(5);
    internal const int MaxLaunchPayloadCharacters = 64 * 1024;
    private readonly object _gate = new();
    private readonly Queue<AppLaunchRequest> _pendingLaunchRequests = [];
    private readonly HashSet<Task> _dispatchTasks = [];
    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;
    private readonly string _pipeName;
    private CancellationTokenSource? _serverCancellation;
    private Task? _serverTask;
    private Func<AppLaunchRequest, CancellationToken, Task>? _launchRequestHandler;
    private bool _disposed;

    private AppSingleInstanceCoordinator(Mutex mutex, bool ownsMutex, string pipeName)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
        _pipeName = pipeName;
    }

    public bool IsPrimary => _ownsMutex;

    public static AppSingleInstanceCoordinator CreateForCurrentUser()
        => Create(BuildCurrentUserScope());

    internal static AppSingleInstanceCoordinator Create(string scope)
    {
        var instanceId = ComputeInstanceId(scope);
        var mutex = new Mutex(true, $"Sunder.App.{instanceId}", out var ownsMutex);
        return new AppSingleInstanceCoordinator(mutex, ownsMutex, $"sunder-app-{instanceId}");
    }

    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPrimary || _serverTask is not null)
        {
            return;
        }

        _serverCancellation = new CancellationTokenSource();
        _serverTask = Task.Run(() => ListenAsync(_serverCancellation.Token));
    }

    public void SetLaunchRequestHandler(Func<AppLaunchRequest, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        AppLaunchRequest[] pending;
        lock (_gate)
        {
            _launchRequestHandler = handler;
            pending = _pendingLaunchRequests.ToArray();
            _pendingLaunchRequests.Clear();
        }

        foreach (var request in pending)
        {
            DispatchLaunchRequest(request);
        }
    }

    public async Task<bool> TryForwardLaunchArgumentsAsync(
        IReadOnlyList<string> args,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPrimary)
        {
            return false;
        }

        var deadline = DateTimeOffset.UtcNow + (timeout ?? DefaultForwardTimeout);
        var payload = JsonSerializer.Serialize(new LaunchArgumentsEnvelope([.. args]));
        if (payload.Length > MaxLaunchPayloadCharacters)
        {
            throw new ArgumentException("Forwarded launch arguments exceed the single-instance payload limit.", nameof(args));
        }
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);

                var remaining = deadline - DateTimeOffset.UtcNow;
                var connectTimeoutMilliseconds = (int)Math.Clamp(remaining.TotalMilliseconds, 100, 500);
                await client.ConnectAsync(connectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);

                await using var writer = new StreamWriter(
                    client,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 1024,
                    leaveOpen: false);
                await writer.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            var retryDelay = DateTimeOffset.UtcNow.AddMilliseconds(100) < deadline
                ? TimeSpan.FromMilliseconds(100)
                : deadline - DateTimeOffset.UtcNow;
            if (retryDelay > TimeSpan.Zero)
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(
                    server,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: false);
                var payload = await ReadBoundedPayloadAsync(reader, cancellationToken).ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize<LaunchArgumentsEnvelope>(payload);
                DispatchLaunchRequest(AppLaunchRequestParser.Parse(envelope?.Arguments ?? []));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                AppSessionLog.WriteError("Failed to receive forwarded Sunder launch request.", ex);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            }
        }
    }

    private static async Task<string> ReadBoundedPayloadAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var payload = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return payload.ToString();
            }

            if (payload.Length + read > MaxLaunchPayloadCharacters)
            {
                throw new InvalidDataException("Forwarded launch arguments exceed the single-instance payload limit.");
            }

            payload.Append(buffer, 0, read);
        }
    }

    private void DispatchLaunchRequest(AppLaunchRequest request)
    {
        Func<AppLaunchRequest, CancellationToken, Task>? handler;
        lock (_gate)
        {
            handler = _launchRequestHandler;
            if (handler is null)
            {
                _pendingLaunchRequests.Enqueue(request);
                return;
            }
        }

        var cancellationToken = _serverCancellation?.Token ?? CancellationToken.None;
        var task = Task.Run(async () =>
        {
            try
            {
                await handler(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Failed to handle forwarded Sunder launch request.", ex);
            }
        }, cancellationToken);
        lock (_gate)
        {
            _dispatchTasks.RemoveWhere(candidate => candidate.IsCompleted);
            _dispatchTasks.Add(task);
        }
    }

    private static string BuildCurrentUserScope()
        => string.Join(
            '\n',
            Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    private static string ComputeInstanceId(string scope)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(scope));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _serverCancellation?.Cancel();
        Task[] dispatchTasks;
        lock (_gate)
        {
            dispatchTasks = _dispatchTasks.ToArray();
            _dispatchTasks.Clear();
        }
        try
        {
            _serverTask?.Wait(TimeSpan.FromSeconds(1));
            Task.WaitAll(dispatchTasks, TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Shutdown should not be blocked by launch handoff cleanup.
        }

        _serverCancellation?.Dispose();
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _mutex.Dispose();
    }

    private sealed record LaunchArgumentsEnvelope(string[] Arguments);
}
