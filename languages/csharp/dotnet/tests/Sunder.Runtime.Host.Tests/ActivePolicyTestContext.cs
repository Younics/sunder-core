using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Sunder.Core.TestSupport;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Tests;

internal sealed class ActivePolicyTestContext : IDisposable
{
    internal const string Origin = "https://registry.example";
    internal static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-09-15T12:00:00Z");
    internal static readonly RegistryPackageVersionIdentity Package = new("example.package", "1.0.0", Hash("package"));
    internal static readonly RegistryPlatformCatalogIdentity Platform = new("sunder.core", "1.1.0", Hash("catalog"));
    private readonly TemporaryDirectoryScope _directory = TemporaryDirectoryScope.Create("sunder-active-policy");
    private readonly HttpClient _http;

    internal ActivePolicyTestContext()
    {
        Paths = new(_directory.RootPath);
        Cache = new(Paths, Clock);
        _http = new(new Handler((message, token) => Send(message, token))) { Timeout = Timeout.InfiniteTimeSpan };
        Client = new(new(new Factory(_http), new(), Time), Clock, Time, Cache);
        Send = async (message, token) =>
        {
            var request = (await message.Content!.ReadFromJsonAsync<RegistryPolicySynchronizationRequest>(RegistryActivePolicyClient.Json, token))!;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Response(request), options: RegistryActivePolicyClient.Json) };
        };
    }

    internal RuntimePackagePaths Paths { get; }
    internal MutablePolicyClock Clock { get; } = new();
    internal PolicyTestTimeProvider Time { get; } = new();
    internal PackageLifecyclePolicyCache Cache { get; }
    internal RegistryActivePolicyClient Client { get; }
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send { get; set; } = null!;
    internal Func<RegistryPolicySynchronizationRequest, RegistryPolicySynchronizationResponse> Response { get; set; } = request => Allowed(request);

    internal async Task<RegistryPolicyObservation> FetchAsync(IReadOnlyList<RegistryPackageVersionIdentity>? packages = null,
        IReadOnlyList<RegistryPlatformCatalogIdentity>? platforms = null, CancellationToken token = default)
        => await Client.SynchronizeAsync(Origin, await Cache.CreateRequestAsync(Origin, packages ?? [Package], platforms ?? [], token), token);

    internal async Task RefreshAsync(IReadOnlyList<RegistryPackageVersionIdentity>? packages = null,
        IReadOnlyList<RegistryPlatformCatalogIdentity>? platforms = null)
        => await Cache.ObserveAsync(await FetchAsync(packages, platforms));

    internal void Require(RegistryPackageVersionIdentity? package = null)
        => Cache.RequireActivation(Origin, package ?? Package);

    internal static RegistryPolicySynchronizationResponse Allowed(RegistryPolicySynchronizationRequest request, long revision = 1)
        => new(request.Nonce, Origin, new(revision, Hash("package-policy-" + revision)),
            new(revision, Hash("checkpoint-" + revision), 1, Hash("topology")), ObservedAt, ObservedAt.AddHours(24),
            request.Packages.Select(subject => new RegistryPackagePolicyDecision(subject, "Active", "Listed", "Allowed", null, null, request.PlatformCatalogs)).ToArray(),
            request.PlatformCatalogs.Select(subject => new RegistryPlatformPolicyDecision(subject, "Supported", "Allowed", null, null)).ToArray());

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void Dispose() { _http.Dispose(); _directory.Dispose(); }

    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}

internal sealed class MutablePolicyClock : IPolicyContinuousClock
{
    internal PolicyClockReading Value { get; set; } = new("boot-1", "test.continuous", 1000, 100_000);
    internal bool Unavailable { get; set; }
    internal Func<PolicyClockReading>? ReadOverride { get; set; }
    public PolicyClockReading Read() => Unavailable ? throw new IOException("Clock unavailable.") : ReadOverride?.Invoke() ?? Value;
    internal void Advance(TimeSpan elapsed) => Value = Value with { Ticks = checked(Value.Ticks + (long)(elapsed.TotalSeconds * Value.Frequency)) };
}

internal sealed class PolicyTestTimeProvider : TimeProvider
{
    private readonly List<Timer> _timers = [];
    private readonly object _gate = new();
    private TimeSpan _elapsed;
    internal DateTimeOffset UtcNow { get; set; } = ActivePolicyTestContext.ObservedAt;
    public override DateTimeOffset GetUtcNow() => UtcNow;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }
    internal void Advance(TimeSpan elapsed)
    {
        Timer[] timers;
        lock (_gate) { _elapsed += elapsed; UtcNow += elapsed; timers = _timers.ToArray(); }
        foreach (var timer in timers) timer.Fire();
    }
    private sealed class Timer(PolicyTestTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan? _due;
        private TimeSpan _period;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        { lock (owner._gate) { _due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._elapsed + dueTime; _period = period; return true; } }
        internal void Fire()
        {
            lock (owner._gate)
            {
                if (_due is not { } due || due > owner._elapsed) return;
                _due = _period > TimeSpan.Zero ? owner._elapsed + _period : null;
            }
            callback(state);
        }
        public void Dispose() { lock (owner._gate) { _due = null; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
