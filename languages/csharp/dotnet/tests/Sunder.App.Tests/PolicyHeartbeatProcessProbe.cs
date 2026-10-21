using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

internal static class PolicyHeartbeatProcessProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", name.Name + ".dll"));
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try { return Run(args); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        var mode = args[0];
        if (mode == "deadline") return DeadlineProbe();
        var seconds = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 7;
        Require(mode is "replacement" or "unavailable", "Unknown scenario.");
        Console.Error.WriteLine("policy-probe stage=starting");
        using var servers = new Servers();
        Console.Error.WriteLine("policy-probe stage=servers-ready");
        using var gateway = new RuntimeClientTransport(() => servers.Gateway);
        using var transport = new AppRuntimeProcessTransport(gateway, _ => servers.Worker);
        var connection = new Connection(transport);
        using var first = new ManualResetEventSlim();
        using var failed = new ManualResetEventSlim();
        using var busy = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ThreadPool.GetMinThreads(out var originalMinimum, out var minimumIo);
        ThreadPool.GetMaxThreads(out var originalMaximum, out var maximumIo);
        var diagnostics = new ConcurrentQueue<string>();
        var failures = new ConcurrentQueue<string>();
        var supervisor = new AppProcessPolicySupervisor(connection,
            runtime => new(Environment.ProcessId, "isolated-probe", new string('b', 64), "probe", Guid.NewGuid(), runtime),
            () => throw new InvalidOperationException("Unexpected policy shutdown."),
            message => { failures.Enqueue(message); Console.Error.WriteLine(message); failed.Set(); }, RuntimeContinuousClock.Read, TimeProvider.System,
            message => { diagnostics.Enqueue(message); if (message.Contains("event=heartbeat-healthy;", StringComparison.Ordinal)) first.Set(); });
        var snapshot = new PackageUiSnapshotDescriptor("probe.package", PackageSourceKind.Dev, 1,
            new("app", "osx-arm64", "dotnet", "probe.dll", "net10.0", null, []),
            new string('a', 64), new string('a', 32), "") { PolicySubject = new("probe.package", "1.0.0", new string('a', 64)) };
        try
        {
            supervisor.RequireLoadAsync(snapshot, default).GetAwaiter().GetResult();
            Console.Error.WriteLine("policy-probe stage=load-admitted");
            Require(first.Wait(TimeSpan.FromSeconds(3)), "No initial authenticated heartbeat.");
            Console.Error.WriteLine("policy-probe stage=first-heartbeat");
            Require(ThreadPool.SetMinThreads(1, minimumIo) && ThreadPool.SetMaxThreads(1, maximumIo), "Could not cap this child-only pool.");
            ThreadPool.QueueUserWorkItem(_ => { busy.Set(); release.Wait(); });
            Require(busy.Wait(TimeSpan.FromSeconds(3)), "No held worker.");
            ThreadPool.GetAvailableThreads(out var available, out _);
            Require(available == 0, "The child pool was not actually saturated.");
            Console.Error.WriteLine("policy-probe stage=pool-held");
            var before = Volatile.Read(ref servers.Heartbeats);
            var started = Stopwatch.GetTimestamp();
            Require(!failed.Wait(TimeSpan.FromSeconds(seconds)), "Healthy supervision failed under idle/pool saturation. " + string.Join("; ", failures));
            var after = Volatile.Read(ref servers.Heartbeats);
            Console.Error.WriteLine("policy-probe stage=healthy-window-finished");
            // Real exchanges add time before the one-second wait; do not turn a
            // long security soak into an exact one-Hz throughput benchmark. The
            // armed production watchdog independently bounds every proof gap.
            Require(after >= before + Math.Max(1, seconds / RuntimePolicyTiming.AppHeartbeatGraceSeconds),
                "Policy transport did not progress while the pool was occupied.");

            // A real temporary HTTP failure must recover on the same policy thread
            // without releasing the held pool worker or replacing session identity.
            servers.Unavailable = true;
            Thread.Sleep(1200);
            servers.Unavailable = false;
            Thread.Sleep(1500);
            Require(failures.IsEmpty && Volatile.Read(ref servers.Heartbeats) > after, "Transient policy transport failure did not recover.");

            if (mode == "replacement") servers.ReplaceRuntime();
            else servers.Unavailable = true;
            Require(failed.Wait(TimeSpan.FromSeconds(7)), "Real policy loss did not fail closed within the fixed grace.");
            Require(failures.Count == 1, "The terminal decision was not exact-once.");
            var terminal = failures.Single();
            Require(terminal.Contains(mode == "replacement" ? "event=runtime-instance-changed;" : "event=heartbeat-grace-expired;", StringComparison.Ordinal), terminal);
            Require(servers.Errors.IsEmpty, string.Join("; ", servers.Errors));
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                mode, runtime = RuntimeInformation.FrameworkDescription, healthySeconds = seconds,
                heartbeatsBefore = before, heartbeatsAfter = after,
                totalSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
                failures = failures.ToArray(), diagnostics = diagnostics.ToArray(),
                scope = "Real synchronous TCP gateway and private IPC HTTP channels; production App supervisor; child-only pool saturation. No user App/state."
            }, Json));
            return 0;
        }
        finally
        {
            // Restore normal scheduling before cleanup. Lowering the live pool's
            // maximum can leave pre-existing idle workers above that maximum;
            // transport cleanup must not be mistaken for the measured window.
            Require(ThreadPool.SetMaxThreads(originalMaximum, maximumIo) && ThreadPool.SetMinThreads(originalMinimum, minimumIo),
                "Could not restore the child-only pool limits.");
            release.Set();
            Console.Error.WriteLine("policy-probe stage=cleanup-started");
            Require(supervisor.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)), "The probe supervisor did not stop.");
            Console.Error.WriteLine("policy-probe stage=supervisor-stopped");
        }
    }

    private static int DeadlineProbe()
    {
        // Three individually successful 900 ms HTTP replies must not turn the
        // one two-second proof budget into three independent two-second waits.
        using var servers = new Servers(replyDelayMilliseconds: 900);
        using var gateway = new RuntimeClientTransport(() => servers.Gateway);
        using var transport = new AppRuntimeProcessTransport(gateway, _ => servers.Worker);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ThreadPool.GetMinThreads(out var minimum, out var minimumIo);
        ThreadPool.GetMaxThreads(out var maximum, out var maximumIo);
        Require(ThreadPool.SetMinThreads(1, minimumIo) && ThreadPool.SetMaxThreads(1, maximumIo), "Could not cap child-only pool.");
        ThreadPool.QueueUserWorkItem(_ => { held.Set(); release.Wait(); });
        Require(held.Wait(TimeSpan.FromSeconds(3)), "No pool holder.");
        ThreadPool.GetAvailableThreads(out var available, out _);
        var started = Stopwatch.GetTimestamp();
        Exception? failure = null;
        try
        {
            // The transport owns the budget even without a caller timer. Pool
            // pressure alone is not proof that every existing timer is blocked.
            var handshake = transport.HeartbeatHandshake(default);
            transport.Heartbeat(new(Guid.NewGuid().ToString("N"), Guid.NewGuid(), handshake.RuntimeInstanceId), default);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            Require(ThreadPool.SetMaxThreads(maximum, maximumIo) && ThreadPool.SetMinThreads(minimum, minimumIo), "Could not restore child-only pool.");
            release.Set();
        }
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var timedOut = failure is TimeoutException or IOException or SocketException;
        Console.WriteLine(JsonSerializer.Serialize(new { mode = "deadline", timedOut, availablePoolWorkers = available,
            elapsedMilliseconds = elapsed, failureType = failure?.GetType().Name, runtime = RuntimeInformation.FrameworkDescription }, Json));
        return timedOut && elapsed <= 2600 && available == 0 ? 0 : 1;
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Connection(AppRuntimeProcessTransport transport) : IAppPolicyConnection
    {
        public string? DiagnosticPhase => transport.HandshakePhase;
        public Task<RuntimeHandshakeResponse> HandshakeAsync(CancellationToken token) => transport.HandshakeAsync(token);
        public RuntimeHandshakeResponse HeartbeatHandshake(CancellationToken token) => transport.HeartbeatHandshake(token);
        public RuntimeAppPolicySessionStatus Heartbeat(RuntimeAppPolicySessionRequest request, CancellationToken token) => transport.Heartbeat(request, token);
        public Task<RuntimeAppPolicySessionDescriptor> OpenAsync(RuntimeAppPolicySessionOpenRequest request, CancellationToken token)
            => Task.FromResult(new RuntimeAppPolicySessionDescriptor(Guid.NewGuid().ToString("N"), request.Process, "active"));
        public Task<RuntimeAppPackageLoadPermitResult> PermitAsync(RuntimeAppPackageLoadPermitRequest request, CancellationToken token)
            => Task.FromResult(new RuntimeAppPackageLoadPermitResult(true, null, new string('d', 32), request.Session, request.Package, null) { Development = true });
        public Task<RuntimeAppPolicySessionStatus> ShutdownAsync(RuntimeAppPolicySessionRequest request, CancellationToken token)
            => Task.FromResult(new RuntimeAppPolicySessionStatus(request.SessionId, "shutdown-requested", null, true, []));
    }

    private sealed class Servers : IDisposable
    {
        private readonly Socket _gateway = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly Socket? _worker;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "shb-" + Guid.NewGuid().ToString("N")[..12]);
        private readonly Thread _gatewayThread;
        private readonly Thread _workerThread;
        private NamedPipeServerStream? _pipe;
        private RuntimeHandshakeResponse _handshake = new(RuntimeProtocol.Identity, 1, 1, 1, Guid.NewGuid(),
            [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.OfficialRuntimeDownloadsV1], new("Sunder.Runtime.Host", "1.0.0", "1.0.0"));
        private int _stopped;
        private readonly int _replyDelayMilliseconds;
        internal int Heartbeats;
        internal volatile bool Unavailable;
        internal ConcurrentQueue<string> Errors { get; } = new();
        internal RuntimeConnectionInfo Gateway { get; }
        internal RuntimeConnectionInfo Worker { get; }

        internal Servers(int replyDelayMilliseconds = 0)
        {
            _replyDelayMilliseconds = replyDelayMilliseconds;
            Directory.CreateDirectory(_directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _gateway.Bind(new IPEndPoint(IPAddress.Loopback, 0)); _gateway.Listen(8);
            Gateway = new(new Uri($"http://127.0.0.1:{((IPEndPoint)_gateway.LocalEndPoint!).Port}/"), "gateway-probe-secret");
            var endpoint = OperatingSystem.IsWindows()
                ? new RuntimeIpcEndpoint(RuntimeIpcTransportKind.NamedPipe, "sunder-hb-" + Guid.NewGuid().ToString("N"))
                : new RuntimeIpcEndpoint(RuntimeIpcTransportKind.UnixSocket, Path.Combine(_directory, "s"));
            Worker = new(RuntimeIpcEndpoint.LogicalRuntimeUrl, "worker-probe-secret") { IpcEndpoint = endpoint };
            if (!OperatingSystem.IsWindows())
            {
                _worker = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _worker.Bind(new UnixDomainSocketEndPoint(endpoint.Address)); _worker.Listen(8);
            }
            _gatewayThread = Start(() => ServeSockets(_gateway, false));
            _workerThread = Start(() =>
            {
                if (_worker is not null) { ServeSockets(_worker, true); return; }
                while (Volatile.Read(ref _stopped) == 0)
                {
                    using var pipe = new NamedPipeServerStream(endpoint.Address, PipeDirection.InOut);
                    Volatile.Write(ref _pipe, pipe);
                    pipe.WaitForConnection();
                    if (Volatile.Read(ref _stopped) != 0) return;
                    Respond(pipe, true);
                }
            });
        }

        private Thread Start(Action action)
        {
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception error) { if (Volatile.Read(ref _stopped) == 0) Errors.Enqueue(error.ToString()); }
            }) { IsBackground = true, Name = "Sunder policy probe HTTP server" };
            thread.Start(); return thread;
        }

        internal void ReplaceRuntime() => Volatile.Write(ref _handshake, _handshake with { RuntimeInstanceId = Guid.NewGuid() });

        private void ServeSockets(Socket listener, bool worker)
        {
            while (Volatile.Read(ref _stopped) == 0)
            {
                using var socket = listener.Accept();
                if (Volatile.Read(ref _stopped) != 0) return;
                socket.ReceiveTimeout = 3000; socket.SendTimeout = 3000;
                using var stream = new NetworkStream(socket);
                Respond(stream, worker);
            }
        }

        private void Respond(Stream stream, bool worker)
        {
            var header = new StringBuilder();
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                var next = stream.ReadByte();
                Require(next >= 0 && header.Length < 16384, "Invalid HTTP request header.");
                header.Append((char)next);
            }
            var lines = header.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var path = lines[0].Split(' ')[1];
            Require(lines.Any(line => line.Equals("Authorization: Bearer " + (worker ? Worker.BearerToken : Gateway.BearerToken), StringComparison.OrdinalIgnoreCase)), "Wrong HTTP credential.");
            var length = lines.Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                .Select(line => int.Parse(line[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture)).SingleOrDefault();
            Require(length is >= 0 and <= 16384, "Unbounded probe request.");
            var body = new byte[length]; stream.ReadExactly(body);
            var status = "200 OK";
            byte[] response;
            if (path == "/api/handshake") response = JsonSerializer.SerializeToUtf8Bytes(Volatile.Read(ref _handshake), Json);
            else
            {
                Require(worker && path.EndsWith("/heartbeat", StringComparison.Ordinal), "Unexpected policy operation.");
                var request = JsonSerializer.Deserialize<RuntimeAppPolicySessionRequest>(body, Json)!;
                if (Unavailable) { status = "503 Service Unavailable"; response = "{}"u8.ToArray(); }
                else
                {
                    response = JsonSerializer.SerializeToUtf8Bytes(new RuntimeAppPolicySessionStatus(request.SessionId, "active", null, false, []), Json);
                    Interlocked.Increment(ref Heartbeats);
                }
            }
            var framing = worker ? "Transfer-Encoding: chunked" : $"Content-Length: {response.Length}";
            var prefix = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\n{framing}\r\nConnection: close\r\n\r\n");
            if (_replyDelayMilliseconds > 0) Thread.Sleep(_replyDelayMilliseconds);
            stream.Write(prefix);
            // Require a pending refill too, not only a response which happened
            // to be entirely buffered before the dedicated reader ran.
            if (worker) Thread.Sleep(5);
            if (worker)
            {
                stream.Write(Encoding.ASCII.GetBytes(response.Length.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + "\r\n"));
                stream.Write(response); stream.Write("\r\n0\r\n\r\n"u8);
            }
            else stream.Write(response);
            stream.Flush();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            // Closing a socket underneath blocking Accept can spin in Dispose on
            // macOS. Wake the owned listener and join its thread before closing it.
            Wake(_gateway);
            if (_worker is not null) Wake(_worker);
            else
            {
                try
                {
                    using var wake = new NamedPipeClientStream(".", Worker.IpcEndpoint!.Address, PipeDirection.InOut);
                    wake.Connect(1000);
                }
                catch (IOException) { }
                catch (TimeoutException) { }
            }
            Require(_gatewayThread.Join(5000) && _workerThread.Join(5000), "The isolated HTTP servers did not stop.");
            _gateway.Dispose(); _worker?.Dispose(); Volatile.Read(ref _pipe)?.Dispose();
            Directory.Delete(_directory, recursive: true);
        }

        private static void Wake(Socket listener)
        {
            try
            {
                using var wake = new Socket(listener.AddressFamily, SocketType.Stream, ProtocolType.Unspecified);
                wake.Connect(listener.LocalEndPoint!);
            }
            catch (SocketException) { }
        }
    }
}
