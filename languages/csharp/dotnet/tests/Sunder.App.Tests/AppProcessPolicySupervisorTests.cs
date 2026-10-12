using Sunder.App.Services;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppProcessPolicySupervisorTests
{
    [Fact]
    public async Task MissingSupervisionFailsClosedBeforeAnyLoad()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => UnavailableAppPackagePolicy.Instance.RequireLoadAsync(Snapshot(), default));
    }

    [Fact]
    public async Task SessionIsOpenedBeforePermitAndIsReusedAcrossUiGenerations()
    {
        await using var fixture = new Fixture();
        await fixture.Supervisor.RequireLoadAsync(Snapshot(), default);
        await fixture.Supervisor.RequireLoadAsync(Snapshot() with { SessionGeneration = 2 }, default);
        Assert.Equal(1, fixture.Connection.Opens);
        Assert.Equal(2, fixture.Connection.Permits);
        Assert.Equal(0, fixture.Failures);
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("regression")]
    [InlineData("boot")]
    public async Task NativeClockWatchdogFailsFastOnSupervisionLoss(string failure)
    {
        await using var fixture = new Fixture();
        await fixture.Supervisor.RequireLoadAsync(Snapshot(), default);
        fixture.Now = failure switch
        {
            "expiry" => fixture.Now with { Ticks = fixture.Now.Ticks + 5000 },
            "regression" => fixture.Now with { Ticks = fixture.Now.Ticks - 1 },
            _ => fixture.Now with { BootSessionId = "changed" },
        };
        fixture.Supervisor.CheckWatchdog();
        Assert.Equal(1, fixture.Failures);
    }

    [Fact]
    public async Task WrongPermitIdentityCannotAuthorizeLoading()
    {
        await using var fixture = new Fixture();
        fixture.Connection.WrongIdentity = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.RequireLoadAsync(Snapshot(), default));
    }

    [Fact]
    public async Task DevelopmentPermitCannotAuthorizeAnInstalledPackage()
    {
        await using var fixture = new Fixture();
        fixture.Connection.Development = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.RequireLoadAsync(Snapshot(), default));
        await fixture.Supervisor.RequireLoadAsync(Snapshot() with { SourceKind = PackageSourceKind.Dev }, default);
    }

    [Fact]
    public async Task DevelopmentImportPermitCanCarryAValidatedPlatformLease()
    {
        await using var fixture = new Fixture();
        fixture.Connection.Development = true; fixture.Connection.DevelopmentPlatformLease = true;
        await fixture.Supervisor.RequireLoadAsync(Snapshot() with { SourceKind = PackageSourceKind.Dev }, default);
        Assert.Equal(0, fixture.Failures);
        fixture.Now = fixture.Now with { Ticks = 86_410_000 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.RequireLoadAsync(Snapshot() with { SourceKind = PackageSourceKind.Dev }, default));
    }

    [Fact]
    public async Task LatePermitCannotReviveExpiredSupervisionBeforeTheWatchdogRuns()
    {
        await using var fixture = new Fixture();
        await fixture.Supervisor.RequireLoadAsync(Snapshot(), default);
        fixture.Connection.BeforePermit = () => fixture.Now = fixture.Now with { Ticks = fixture.Now.Ticks + 5000 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.RequireLoadAsync(Snapshot(), default));
        Assert.Equal(1, fixture.Failures);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.RequireLoadAsync(Snapshot(), default));
    }

    private static PackageUiSnapshotDescriptor Snapshot() => new("example.app", PackageSourceKind.Installed, 1, RuntimeContractTestData.AppTarget(), new('a', 64), new('a', 32), "")
    { PolicySubject = new("example.app", "1.0.0", new('a', 64)) };

    private sealed class Fixture : IAsyncDisposable
    {
        internal RuntimeContinuousClockReading Now = new("boot", "test", 1000, 10000);
        internal FakeConnection Connection { get; } = new();
        internal AppProcessPolicySupervisor Supervisor { get; }
        internal int Failures;
        internal Fixture() => Supervisor = new(Connection, runtime => new(123, "birth", new('b', 64), "build", Guid.NewGuid(), runtime),
            () => { }, _ => Interlocked.Increment(ref Failures), () => Now, TimeProvider.System);
        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisposeAsync();
            Now = Now with { Ticks = Now.Ticks + 10000 };
            Supervisor.CheckWatchdog();
        }
    }

    private sealed class FakeConnection : IAppPolicyConnection
    {
        private readonly Guid _runtime = Guid.NewGuid();
        internal int Opens;
        internal int Permits;
        internal bool WrongIdentity;
        internal bool Development;
        internal bool DevelopmentPlatformLease;
        internal Action? BeforePermit;
        public Task<RuntimeHandshakeResponse> HandshakeAsync(CancellationToken token) => Task.FromResult(new RuntimeHandshakeResponse(RuntimeProtocol.Identity,
            1, 1, 1, _runtime, [], new("Sunder.Runtime.Host", "1.0.0", "1.0.0")));
        public Task<RuntimeAppPolicySessionDescriptor> OpenAsync(RuntimeAppPolicySessionOpenRequest request, CancellationToken token)
        { Opens++; return Task.FromResult(new RuntimeAppPolicySessionDescriptor(new('c', 32), request.Process, "active")); }
        public Task<RuntimeAppPolicySessionStatus> HeartbeatAsync(RuntimeAppPolicySessionRequest request, CancellationToken token)
            => Task.FromResult(new RuntimeAppPolicySessionStatus(request.SessionId, "active", null, false, []));
        public Task<RuntimeAppPackageLoadPermitResult> PermitAsync(RuntimeAppPackageLoadPermitRequest request, CancellationToken token)
        {
            Assert.True(Opens > 0);
            Permits++;
            BeforePermit?.Invoke();
            return Task.FromResult(new RuntimeAppPackageLoadPermitResult(true, null, new('d', 32), request.Session,
                WrongIdentity ? request.Package with { Version = "2.0.0" } : request.Package,
                Development && !DevelopmentPlatformLease ? null : new("valid", null, new("https://registry.example", 1, new('a', 64), 1, new('b', 64), 1, new('c', 64)),
                    new('d', 32), new('e', 64), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(24), new("boot", "test", 1000, 10000, 86410000, 10000, null))) { Development = Development });
        }
        public Task<RuntimeAppPolicySessionStatus> ShutdownAsync(RuntimeAppPolicySessionRequest request, CancellationToken token)
            => Task.FromResult(new RuntimeAppPolicySessionStatus(request.SessionId, "shutdown-requested", PackagePolicyErrorCodes.AppProcessTerminationRequired, true, []));
    }
}
