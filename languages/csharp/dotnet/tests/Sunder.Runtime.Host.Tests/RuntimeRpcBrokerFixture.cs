using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using static Sunder.Runtime.Host.Tests.RuntimeRpcTestPackages;

namespace Sunder.Runtime.Host.Tests;

internal static class RuntimeRpcBrokerFixture
{
    internal sealed class RetiringRpcFixture : IAsyncDisposable
    {
        internal RpcFixture Raw { get; } = new();
        internal ActivePolicyTestContext Policy { get; } = new();
        internal AppPolicyPeer Peer { get; } = new(123, null);
        internal AppProcessPolicySessions Policies { get; private set; } = null!;
        internal RuntimeRpcAppSessionManager Manager { get; private set; } = null!;
        internal RuntimeRpcBroker Broker { get; private set; } = null!;
        internal RuntimeAppPolicySessionRequest Session { get; private set; } = null!;
        private RuntimePackageSource _source = null!;
        private PackageTargetDescriptor _target = null!;
        private RuntimeRpcPermissionStore _permissions = null!;
        private RuntimeContentTransferStore _content = null!;
        internal static async Task<RetiringRpcFixture> CreateAsync(TestHandler handler, IAppPolicyProcessController? process = null)
        {
            var value = new RetiringRpcFixture();
            var published = await value.Raw.PublishAsync(handler, webCaller: true, callerSourceKind: PackageSourceKind.Dev,
                transform: package => package.Descriptor.PackageId == "caller.package" ? package with { Source = package.Source with { ContentIdentity = new('c', 64) } } : package);
            value._source = published.Session.LoadedPackageMap["caller.package"].Source;
            value._target = PackageTargetSelection.ToDescriptor(value._source.Manifest!.Targets!.Single()!);
            value._permissions = new(value.Policy.Paths);
            value._content = new(value.Policy.Paths);
            var protocol = new RuntimeProtocolDescriptor();
            value.Policies = new(value.Policy.Paths, protocol, new RequiredProviderSnapshots(value._source), new(value.Policy.Cache), value.Policy.Cache,
                value.Policy.Clock, value.Policy.Time, process ?? new RequiredProviderProcess(), NullLogger<AppProcessPolicySessions>.Instance, value.Raw.Owner.State, new(value._permissions));
            var identity = new RuntimeAppProcessIdentity(123, "birth", new('a', 64), "test", Guid.NewGuid(), protocol.RuntimeInstanceId);
            var session = await value.Policies.OpenAsync(new(identity), value.Peer, default);
            value.Session = new(session.SessionId, identity.AppGenerationId, protocol.RuntimeInstanceId);
            value.Manager = new(value.Raw.Owner.State, new(), value.Policies);
            value.Broker = new(value.Raw.Catalog, value._permissions, value.Raw.Owner.State, value.Raw.Owner, appSessions: value.Manager, contentStore: value._content);
            return value;
        }
        internal Task<RuntimeAppPackageLoadPermitResult> PermitAsync(Guid id) => Policies.PermitAsync(new(Session, PackageUiSnapshotStore.PolicySubject(_source)!)
            { SnapshotId = new('a', 32), AppActivationId = id, AppTarget = _target }, Peer, default);
        internal RuntimeRpcAppSessionOpenRequest OpenRequest(Guid id, RuntimeAppPackageLoadPermitResult permit)
            => new(_source.PackageId, "1.0.0", RuntimeRpcPackageMetadata.GetManifestSha256(_source.ContentIndex!), _target, Raw.Owner.Generation, id) { LoadPermitId = permit.PermitId };
        internal Task<RuntimeRpcAppSessionDescriptor> OpenAsync(Guid id, RuntimeAppPackageLoadPermitResult permit) => Manager.OpenAsync(OpenRequest(id, permit), default, Peer);
        public async ValueTask DisposeAsync() { Manager.Dispose(); Policies.Dispose(); _content.Dispose(); _permissions.Dispose(); await Raw.DisposeAsync(); Policy.Dispose(); }
    }

    internal sealed class PublicationWorker : TestRuntimePackageWorker
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task GenerationCompletion => _completion.Task;
    }

    internal sealed class RequiredProviderSnapshots(RuntimePackageSource source) : IAppPolicySnapshotSource
    {
        internal RuntimePackageSource? Source { get; set; } = source;
        public RuntimePackageSource? PolicySource(string snapshotId) => snapshotId == new string('a', 32) ? Source : null;
    }

    internal sealed class RequiredProviderProcess : IAppPolicyProcessController, IAppPolicyProcessHandle
    {
        internal AppPolicyProcessState State = AppPolicyProcessState.Alive;
        internal int Terminations;
        public IAppPolicyProcessHandle Attach(RuntimeAppProcessIdentity identity, AppPolicyPeer peer, bool recovery) => this;
        public AppPolicyProcessState Probe() => State;
        public void Terminate() => Terminations++;
        public void Dispose() { }
    }

    internal static async IAsyncEnumerable<JsonElement> Values(
        JsonElement first,
        JsonElement second,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return first;
        await Task.Yield();
        yield return second;
    }

    internal static async Task DrainAsync<T>(System.Threading.Channels.ChannelReader<T> reader)
    {
        await foreach (var _ in reader.ReadAllAsync())
        {
        }
    }

    internal static string CreateRoot()
        => Path.Combine(Path.GetTempPath(), "sunder-rpc-tests", Guid.NewGuid().ToString("N"));

    internal static RuntimeRpcPermissionKey PermissionKey(
        string action,
        string callerRole = SunderPackageFormat.RuntimeHostRole,
        string packageId = "caller.package",
        string packageVersion = "1.0.0",
        string? manifestSha256 = null,
        string contractId = "example.rpc",
        string contractVersion = "1.0.0",
        string? abiSha256 = null)
        => new(
            packageId,
            packageVersion,
            manifestSha256 ?? new string('a', 64),
            callerRole,
            contractId,
            contractVersion,
            abiSha256 ?? Contract().AbiSha256!,
            action);

    internal sealed class RpcFixture : IAsyncDisposable
    {
        private readonly string _root = CreateRoot();
        private readonly RuntimeRpcPermissionStore _permissions;
        private readonly RuntimeContentTransferStore _transfers;
        private ActivePackageSession? _activeSession;

        public RpcFixture(
            RuntimeRpcPolicyOptions? policy = null,
            TimeProvider? timeProvider = null,
            RuntimeTransportPolicyOptions? transportPolicy = null,
            PackagePolicyAdmission? packagePolicy = null)
        {
            Directory.CreateDirectory(_root);
            timeProvider ??= TimeProvider.System;
            Catalog = new RuntimeRpcCatalog();
            var events = new RuntimeEventStreamService();
            OwnerLogger = new CapturingLogger<RuntimeSessionOwner>();
            Owner = new RuntimeSessionOwner(
                OwnerLogger,
                events,
                rpcCatalog: Catalog);
            _permissions = new RuntimeRpcPermissionStore(new RuntimePackagePaths(_root));
            transportPolicy ??= new RuntimeTransportPolicyOptions();
            _transfers = new RuntimeContentTransferStore(
                new RuntimePackagePaths(_root),
                transportPolicy,
                timeProvider: timeProvider);
            AppSessions = new RuntimeRpcAppSessionManager(Owner.State, policy ?? new RuntimeRpcPolicyOptions());
            Broker = new RuntimeRpcBroker(
                Catalog,
                _permissions,
                Owner.State,
                Owner,
                policy,
                timeProvider,
                CancellationToken.None,
                AppSessions,
                _transfers,
                transportPolicy,
                packagePolicy);
        }

        public RuntimeRpcCatalog Catalog { get; }
        public CapturingLogger<RuntimeSessionOwner> OwnerLogger { get; }
        public RuntimeSessionOwner Owner { get; }
        public RuntimeRpcBroker Broker { get; }
        public RuntimeRpcAppSessionManager AppSessions { get; }
        public string PermissionFilePath => new RuntimePackagePaths(_root).RpcPermissionFilePath;
        public string[] GetTransferFiles()
        {
            var path = new RuntimePackagePaths(_root).TransferRootPath;
            return Directory.Exists(path) ? Directory.GetFiles(path) : [];
        }

        public async Task<RpcActivation> PublishAsync(
            TestHandler handler,
            bool providerUsesContract = false,
            bool webCaller = false,
            PackageSourceKind callerSourceKind = PackageSourceKind.Installed,
            TestHandler? siblingHandler = null,
            string callerPackageId = "caller.package",
            TestHandler? healthyHandler = null,
            SunderRpcContractDescriptor? callerContract = null,
            bool runtimeCallerUsesContract = true,
            bool appCallerUsesContract = true,
            string providerAudience = SunderPackageFormat.PublicProviderAudience,
            SunderRpcContractDescriptor? providerContract = null,
            IReadOnlyList<string>? providerRequiredHostCapabilities = null,
            Func<ActiveLoadedPackage, ActiveLoadedPackage>? transform = null,
            IReadOnlyList<ActiveLoadedPackage>? additionalPackages = null)
        {
            var descriptor = providerContract ?? Contract();
            callerContract ??= descriptor;
            var caller = CreatePackage(
                callerPackageId,
                callerContract,
                null,
                usesContract: runtimeCallerUsesContract,
                sourceKind: callerSourceKind);
            if (webCaller)
            {
                caller = AddWebTarget(caller, callerContract, appCallerUsesContract);
            }
            var provider = CreatePackage(
                "provider.package",
                descriptor,
                handler,
                providerUsesContract,
                siblingHandler: siblingHandler,
                providerAudience: providerAudience,
                requiredHostCapabilities: providerRequiredHostCapabilities);
            var healthy = healthyHandler is null
                ? null
                : CreatePackage(
                    "healthy.package",
                    descriptor,
                    healthyHandler,
                    usesContract: false,
                    providerId: "healthy.provider");
            caller = transform?.Invoke(caller) ?? caller;
            provider = transform?.Invoke(provider) ?? provider;
            if (healthy is not null) healthy = transform?.Invoke(healthy) ?? healthy;
            var packages = new List<ActiveLoadedPackage> { caller, provider };
            if (healthy is not null) packages.Add(healthy);
            packages.AddRange(additionalPackages ?? []);
            var session = CreateSession(packages.ToArray());
            await Owner.PublishAsync(
                session,
                Owner.Sources.Snapshot(),
                [],
                [],
                Owner.Generation);
            Catalog.ActivateSession(session, Owner.Generation);
            _activeSession = session;
            return new RpcActivation(
                new RuntimeRpcCallerStamp(caller.Descriptor.PackageId, caller.RuntimeActivationId),
                new RuntimeRpcCallerStamp(provider.Descriptor.PackageId, provider.RuntimeActivationId),
                handler,
                session);
        }

        public RuntimeRpcClient CreateClient(RuntimeRpcCallerStamp stamp) => new(Broker, stamp);

        public Task GrantAsync(string action)
            => GrantAsync("caller.package", action);

        public Task GrantAsync(string packageId, string action)
            => GrantAsync(packageId, new string('a', 64), action);

        public Task GrantAsync(
            string packageId,
            string manifestSha256,
            string action,
            string callerRole = SunderPackageFormat.RuntimeHostRole)
            => _permissions.SetAsync(
                ActivePermissionKey(packageId, manifestSha256, callerRole, action),
                RuntimeRpcPermissionState.Granted,
                CancellationToken.None);

        public Task<(RuntimeRpcAppSessionDescriptor Session, string ManifestSha256, PackageTargetDescriptor Target)> OpenAppCallerAsync(
            string packageId = "caller.package")
        {
            var source = _activeSession!.LoadedPackageMap[packageId].Source;
            var target = source.Manifest!.Targets!.Single()!;
            var manifestSha256 = RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex!);
            var descriptor = PackageTargetSelection.ToDescriptor(target);
            return OpenAsync();

            async Task<(RuntimeRpcAppSessionDescriptor, string, PackageTargetDescriptor)> OpenAsync()
            {
                var session = await AppSessions.OpenAsync(
                    new RuntimeRpcAppSessionOpenRequest(
                        packageId,
                        "1.0.0",
                        manifestSha256,
                        descriptor,
                        Owner.Generation,
                        Guid.NewGuid()),
                    CancellationToken.None);
                return (session, manifestSha256, descriptor);
            }
        }

        public Task RevokeAsync(string action, string? manifestSha256 = null, string callerRole = SunderPackageFormat.RuntimeHostRole)
            => _permissions.SetAsync(
                ActivePermissionKey(
                    "caller.package",
                    manifestSha256 ?? new string('a', 64),
                    callerRole,
                    action),
                RuntimeRpcPermissionState.Denied,
                CancellationToken.None);

        private RuntimeRpcPermissionKey ActivePermissionKey(
            string packageId,
            string manifestSha256,
            string callerRole,
            string action)
        {
            var package = _activeSession!.LoadedPackageMap[packageId];
            var uses = callerRole == SunderPackageFormat.RuntimeHostRole
                ? package.RpcContractUses
                : package.Source.Manifest!.UsesContracts ?? [];
            var use = uses.Where(static candidate => candidate is not null)
                .Select(static candidate => candidate!)
                .Single(candidate =>
                string.Equals(candidate.Role, callerRole, StringComparison.Ordinal)
                && string.Equals(candidate.ContractId, "example.rpc", StringComparison.Ordinal)
                && (candidate.Actions ?? []).Contains(action, StringComparer.Ordinal));
            return new RuntimeRpcPermissionKey(
                package.Descriptor.PackageId,
                package.Descriptor.Version,
                manifestSha256,
                callerRole,
                use.ContractId!,
                use.ContractVersion!,
                use.AbiSha256!,
                action);
        }

        public Task<RuntimeRpcPermissionSnapshot> SetPermissionAsync(
            RuntimeRpcPermissionUpdateRequest request,
            RuntimeRpcPermissionState state)
            => new RuntimeRpcPermissionService(Owner.State, _permissions)
                .SetAsync(request, state, CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            if (_activeSession is not null)
            {
                try
                {
                    await Owner.State.ClearActiveSessionAsync();
                }
                catch
                {
                }
            }
            Catalog.Dispose();
            AppSessions.Dispose();
            _permissions.Dispose();
            _transfers.Dispose();
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }

    internal sealed record RpcActivation(
        RuntimeRpcCallerStamp Caller,
        RuntimeRpcCallerStamp Provider,
        TestHandler Handler,
        ActivePackageSession Session);

    internal sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public Action? OnTimerCreated { get; set; }

        public int ActiveTimerCount
        {
            get
            {
                lock (_gate) return _timers.Count(timer => !timer.IsDisposed);
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _utcNow;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
                ChangeLocked(timer, dueTime, period);
            }
            OnTimerCreated?.Invoke();
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
            lock (_gate) _utcNow += duration;
            while (true)
            {
                ManualTimer[] due;
                lock (_gate)
                {
                    due = _timers
                        .Where(timer => !timer.IsDisposed && timer.DueAtUtc is { } at && at <= _utcNow)
                        .ToArray();
                    foreach (var timer in due)
                    {
                        if (timer.Period == Timeout.InfiniteTimeSpan)
                        {
                            timer.DueAtUtc = null;
                        }
                        else
                        {
                            do
                            {
                                timer.DueAtUtc += timer.Period;
                            } while (timer.DueAtUtc <= _utcNow);
                        }
                    }
                }
                if (due.Length == 0) return;
                foreach (var timer in due) timer.Invoke();
            }
        }

        private bool Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                if (timer.IsDisposed) return false;
                ChangeLocked(timer, dueTime, period);
                return true;
            }
        }

        private void ChangeLocked(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            timer.DueAtUtc = dueTime == Timeout.InfiniteTimeSpan ? null : _utcNow + dueTime;
            timer.Period = period;
        }

        private void Dispose(ManualTimer timer)
        {
            lock (_gate)
            {
                if (timer.IsDisposed) return;
                timer.IsDisposed = true;
                timer.DueAtUtc = null;
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            public bool IsDisposed { get; set; }
            public DateTimeOffset? DueAtUtc { get; set; }
            public TimeSpan Period { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
                => owner.Change(this, dueTime, period);

            public void Dispose() => owner.Dispose(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Invoke()
            {
                if (!IsDisposed) callback(state);
            }
        }
    }

    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly object _gate = new();
        private readonly List<CapturedLogEntry> _entries = [];

        public Action<CapturedLogEntry>? OnLog { get; set; }

        public IReadOnlyList<CapturedLogEntry> Entries
        {
            get
            {
                lock (_gate) return _entries.ToArray();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = new CapturedLogEntry(logLevel, eventId, formatter(state, exception), exception);
            lock (_gate) _entries.Add(entry);
            OnLog?.Invoke(entry);
        }
    }

    internal sealed record CapturedLogEntry(
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception? Exception);

    internal sealed class TestHandler : ISunderRpcServiceHandler
    {
        public Func<JsonElement, CancellationToken, ValueTask<JsonElement>> Unary { get; set; } =
            static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
            };

        public Func<SunderRpcInvocationContext, JsonElement, CancellationToken, ValueTask<JsonElement>>? UnaryWithContext { get; set; }

        public Func<JsonElement, CancellationToken, IAsyncEnumerable<JsonElement>> Stream { get; set; } =
            static (_, cancellationToken) => Single(cancellationToken);

        public Func<SunderRpcInvocationContext, JsonElement, CancellationToken, IAsyncEnumerable<JsonElement>>? StreamWithContext { get; set; }

        public ValueTask<JsonElement> InvokeUnaryAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            CancellationToken cancellationToken = default)
            => UnaryWithContext is null
                ? Unary(request, cancellationToken)
                : UnaryWithContext(context, request, cancellationToken);

        public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            CancellationToken cancellationToken = default)
            => StreamWithContext is null
                ? Stream(request, cancellationToken)
                : StreamWithContext(context, request, cancellationToken);

        private static async IAsyncEnumerable<JsonElement> Single(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return JsonSerializer.SerializeToElement(new { accepted = true });
            await Task.CompletedTask;
        }
    }
}
