using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.Views.Controls;
using Sunder.Core.TestSupport;
using Sunder.Package.Projections;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.App.Tests;

public sealed partial class PackageViewHostServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("worker-execution-observations.v1")]
    [InlineData("package-development-revisions.v1")]
    [InlineData("package-development-reload.v1")]
    [InlineData("package-development-logs.v1")]
    [InlineData("package-development-log-queries.v1")]
    [InlineData("package-development-views.v1")]
    [InlineData("app-activation-services.v1")]
    public void ObservationTargetsRequireTrackedCallerRetentionRatherThanNullLifetimeFallback(string? capability)
    {
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, "unused") with
        { ActivationFingerprint = new('a', 64) };
        if (capability is not null)
        {
            var target = source.Target;
            source = source with { Target = new(target.Role, target.Rid, target.Kind, target.EntryPoint,
                target.TargetFramework, target.SdkVersion, [capability], target.Views, target.ExecutionRuntime, target.ExecutionRuntimeBinding) };
        }
        var handle = new AppLoadedPackageHandle(package, source, "unused", null, null);
        Assert.Equal(capability is null, AppPackageGenerationBuilder.CanRetain(handle, package, source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevelopmentRetentionRevalidatesExactActivationWithoutReplacingUnchangedRpcSessions(bool workerEdited)
    {
        using var temporary = TemporaryDirectoryScope.Create("app-worker-edit-retention");
        var folder = CreateAppPackageSource(temporary.RootPath, "agent");
        var manifestPath = Path.Combine(folder, "manifest", "sunder-package.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"core.v1\"",
            "\"core.v1\",\"package-development-revisions.v1\",\"worker-execution-observations.v1\"", StringComparison.Ordinal));
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, folder) with
        {
            ActivationFingerprint = new('a', 64), PolicySubject = new("agent", "1.0.0", new('b', 64)), SourceContentIdentity = new('b', 64),
        };
        var policy = new ExactRetentionPolicy(source);
        using var handler = new RetentionRpcHandler(policy);
        using var transport = new RuntimeClientTransport(() => new(new Uri("http://runtime.test/"), "test-token"), handler);
        var downloads = 0;
        await using var host = new PackageViewHostService(new AppPackageViewRegistry(), [], [], [], temporary.GetPath("session"),
            getRuntimeConnectionInfo: transport.GetConnectionInfo, uiDispatcher: TestUiDispatcher, packagePolicy: policy,
            downloadPackageUiSnapshotAsync: (snapshot, destination, token) =>
            {
                Interlocked.Increment(ref downloads);
                return RuntimeContractTestData.DownloadSnapshotAsync(snapshot, destination, token);
            });
        await host.ApplyPackageDeltaAsync([package], [source]);
        var view = host.GetOrCreateView("agent.chat");
        Assert.NotNull(view);
        var birth = RetentionGeneration(host).State.GetLoadedPackage("agent")!;
        var rpc = Assert.IsType<AppPackageManagedRpcClient>(birth.TargetLifetime);
        var birthStamp = Assert.Single(handler.Opens);
        var endpoint = new SunderRpcEndpointReference("rpc1_exact_provider");
        for (var generation = 2; generation <= 4; generation++)
        {
            var previous = source;
            await using var oldScope = await rpc.CreateCallScopeAsync();
            source = source with
            {
                SessionGeneration = generation, SnapshotId = Guid.NewGuid().ToString("N"),
                PolicySubject = workerEdited ? source.PolicySubject! with { PackageContentIdentity = new((char)('b' + generation), 64) } : source.PolicySubject,
                SourceContentIdentity = workerEdited ? new((char)('b' + generation), 64) : source.SourceContentIdentity,
            };
            source = source with { SnapshotUri = $"packages/ui-snapshots/{source.SnapshotId}" };
            policy.Expected = source;
            await host.ApplyPackageGenerationAsync(CreateRuntimeSnapshot(package, source, generation), [source], null,
                async (candidate, _, token) =>
                {
                    Assert.Contains("agent", candidate.RetainedPackageIds);
                    Assert.Same(previous, RetentionGeneration(host).State.GetLoadedPackage("agent")!.Source);
                    Assert.True(await oldScope.TryReportInvariantViolationAsync(endpoint, new Exception("before publication"), token));
                    return () => Assert.Same(source, RetentionGeneration(host).State.GetLoadedPackage("agent")!.Source);
                }, default);
            await host.WaitForRetirementsAsync();

            var current = RetentionGeneration(host).State.GetLoadedPackage("agent")!;
            Assert.Same(source, current.Source);
            Assert.Same(birth.ServiceProvider, current.ServiceProvider);
            Assert.Same(birth.LoadContext, current.LoadContext);
            Assert.Same(rpc, current.TargetLifetime);
            Assert.Equal(birth.Folder, current.Folder);
            Assert.True(Directory.Exists(birth.Folder));
            Assert.Same(view, host.GetOrCreateView("agent.chat"));
            Assert.Equal("Activated", host.GetAppReadiness("agent", new(CreateRuntimeSnapshot(package, source, generation).RuntimeInstanceId, generation), source.ActivationFingerprint));
            var appliedStamp = new RuntimePackageStamp(CreateRuntimeSnapshot(package, source, generation).RuntimeInstanceId, generation);
            Assert.Equal("Activated", host.GetAppReadiness("agent", appliedStamp, source.ActivationFingerprint, source.SourceContentIdentity));
            Assert.Equal(workerEdited ? "Pending" : "Activated", host.GetAppReadiness("agent", appliedStamp, source.ActivationFingerprint, previous.SourceContentIdentity));
            Assert.True(await rpc.TryReportInvariantViolationAsync(endpoint, new Exception("after publication")));
            Assert.Equal(handler.SessionIds.Last(), handler.Reports.Last().SessionId);
            if (workerEdited)
                await Assert.ThrowsAsync<ObjectDisposedException>(() => oldScope.TryReportInvariantViolationAsync(endpoint, new Exception("old scope")).AsTask());
            else
                Assert.True(await oldScope.TryReportInvariantViolationAsync(endpoint, new Exception("unchanged caller")));
        }
        // An unrelated generation after a Worker edit must compare the published
        // source, not the immutable birth owner's source, and keep its current RPC.
        source = source with { SessionGeneration = 5 };
        policy.Expected = source;
        await host.ApplyPackageDeltaAsync([package], [source]);
        await host.WaitForRetirementsAsync();
        Assert.Same(view, host.GetOrCreateView("agent.chat"));
        Assert.Equal(1, downloads);
        Assert.Equal(5, policy.Requests.Count);
        Assert.Equal(workerEdited ? new long[] { 1, 2, 3, 4 } : [1], handler.Opens.Select(open => open.SessionGeneration));
        Assert.All(handler.Opens, open =>
        {
            Assert.Equal(birthStamp.AppGenerationId, open.AppGenerationId);
            Assert.Equal(birthStamp.ManifestSha256, open.ManifestSha256);
            Assert.False(string.IsNullOrEmpty(open.LoadPermitId));
        });
        Assert.Equal(workerEdited ? 4 : 1, handler.Opens.Select(open => open.LoadPermitId).Distinct().Count());
        Assert.Equal(workerEdited ? 3 : 0, handler.ClosedSessions.Count);

        AppPackageUiRecoveryRequest? recovery = null;
        host.PackageUiRecoveryRequested += request => recovery = request;
        var fault = await Assert.ThrowsAsync<FormatException>(() => InvokePendingCandidateManagedFaultAsync(view));
        Assert.True(host.TryHandleUnhandledException(fault));
        Assert.NotNull(recovery);
        Assert.Equal(source.ContentHash, recovery.ContentHash);
        Assert.Equal(host.CurrentGenerationId, recovery.FaultedGenerationId);
        await recovery.CleanupCompletion;
        await host.ApplyPackageDeltaAsync([], []);
        await host.WaitForRetirementsAsync();
        Assert.True(GetIsDisposed(view));
        Assert.Equal(0, host.LoadedPackageCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => rpc.CreateCallScopeAsync().AsTask());
        Assert.Equal(birthStamp.AppGenerationId, Assert.Single(policy.RetiredActivationIds));
    }

    [Theory]
    [InlineData("permit")]
    [InlineData("rpc")]
    [InlineData("presentation")]
    [InlineData("cancelled")]
    public async Task WorkerOnlyRetentionFailurePreservesPublishedSourceCallerAndBirthOwner(string failure)
    {
        using var temporary = TemporaryDirectoryScope.Create("app-worker-edit-rollback");
        var folder = CreateAppPackageSource(temporary.RootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, folder) with
        {
            ActivationFingerprint = new('a', 64), PolicySubject = new("agent", "1.0.0", new('b', 64)),
        };
        var policy = new ExactRetentionPolicy(source);
        using var handler = new RetentionRpcHandler(policy);
        using var transport = new RuntimeClientTransport(() => new(new Uri("http://runtime.test/"), "test-token"), handler);
        await using var host = new PackageViewHostService(new AppPackageViewRegistry(), [], [], [], temporary.GetPath("session"),
            getRuntimeConnectionInfo: transport.GetConnectionInfo, uiDispatcher: TestUiDispatcher, packagePolicy: policy,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);
        await host.ApplyPackageDeltaAsync([package], [source]);
        var view = host.GetOrCreateView("agent.chat");
        Assert.NotNull(view);
        var original = RetentionGeneration(host);
        var birth = original.State.GetLoadedPackage("agent")!;
        var rpc = Assert.IsType<AppPackageManagedRpcClient>(birth.TargetLifetime);
        await using var scope = await rpc.CreateCallScopeAsync();
        var rejected = source with { SessionGeneration = 2, SnapshotId = Guid.NewGuid().ToString("N"),
            PolicySubject = source.PolicySubject! with { PackageContentIdentity = new('c', 64) } };
        policy.Expected = rejected;
        policy.Deny = failure == "permit";
        handler.FailOpen = failure == "rpc";
        using var cancellation = new CancellationTokenSource();
        var apply = host.ApplyPackageGenerationAsync(CreateRuntimeSnapshot(package, rejected, 2), [rejected], null,
            (_, _, _) =>
            {
                Assert.Same(source, RetentionGeneration(host).State.GetLoadedPackage("agent")!.Source);
                if (failure == "cancelled") cancellation.Cancel();
                return Task.FromResult<Action?>(() =>
                {
                    Assert.Same(rejected, RetentionGeneration(host).State.GetLoadedPackage("agent")!.Source);
                    Assert.Same(source, original.State.GetLoadedPackage("agent")!.Source);
                    throw new InvalidOperationException("presentation rejected");
                });
            }, cancellation.Token);
        if (failure == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        else if (failure == "rpc") await Assert.ThrowsAsync<RuntimeClientException>(() => apply);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => apply);
        await host.WaitForRetirementsAsync();
        Assert.Same(original, RetentionGeneration(host));
        Assert.Same(source, original.State.GetLoadedPackage("agent")!.Source);
        Assert.Same(view, host.GetOrCreateView("agent.chat"));
        Assert.False(GetIsDisposed(view));
        Assert.True(Directory.Exists(birth.Folder));
        var endpoint = new SunderRpcEndpointReference("rpc1_exact_provider");
        Assert.True(await scope.TryReportInvariantViolationAsync(endpoint, new Exception("still published")));
        Assert.Equal(handler.SessionIds.First(), handler.Reports.Last().SessionId);
        Assert.DoesNotContain(handler.SessionIds.First(), handler.ClosedSessions);
        Assert.Empty(policy.RetiredActivationIds);
        Assert.Equal(failure is "presentation" or "cancelled" ? 1 : 0, handler.ClosedSessions.Count);

        policy.Deny = false;
        handler.FailOpen = false;
        var accepted = rejected with { SessionGeneration = 3, SnapshotId = Guid.NewGuid().ToString("N") };
        policy.Expected = accepted;
        await host.ApplyPackageDeltaAsync([package], [accepted]);
        await host.WaitForRetirementsAsync();
        Assert.Same(accepted, RetentionGeneration(host).State.GetLoadedPackage("agent")!.Source);
        Assert.Same(view, host.GetOrCreateView("agent.chat"));
        Assert.Equal(3, policy.Requests.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.TryReportInvariantViolationAsync(endpoint, new Exception("retired scope")).AsTask());
    }

    [Fact]
    public async Task UnchangedObservationActivationDoesNotBlockWebViewPublicationReloadAndUnload()
    {
        using var temporary = TemporaryDirectoryScope.Create("app-node-web-retained-sibling");
        var folder = CreateAppPackageSource(temporary.GetPath("managed"), "agent");
        var manifestPath = Path.Combine(folder, "manifest", "sunder-package.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"core.v1\"",
            "\"core.v1\",\"package-development-revisions.v1\",\"worker-execution-observations.v1\"", StringComparison.Ordinal));
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, folder) with
        { ActivationFingerprint = new('a', 64), PolicySubject = new("agent", "1.0.0", new('b', 64)) };
        var policy = new ExactRetentionPolicy(source);
        using var handler = new RetentionRpcHandler(policy);
        using var transport = new RuntimeClientTransport(() => new(new Uri("http://runtime.test/"), "test-token"), handler);
        await using var host = new PackageViewHostService(new AppPackageViewRegistry(), [], [], [], temporary.GetPath("session"),
            getRuntimeConnectionInfo: transport.GetConnectionInfo, uiDispatcher: TestUiDispatcher, packagePolicy: policy,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);
        await host.ApplyPackageDeltaAsync([package], [source]);
        var managedView = host.GetOrCreateView("agent.chat");
        var managedOwner = RetentionGeneration(host).State.GetLoadedPackage("agent")!;
        var webFolder = temporary.GetPath("node-output");
        Directory.CreateDirectory(Path.Combine(webFolder, "manifest"));
        var webViews = new[] { new PackageWebViewDescriptor("local.node.main", "Node", "/", null, "middle", true) };
        var webTarget = new PackageTargetDescriptor("app", source.Target.Rid, "web", "index.html", "web", "1.1.0",
            ["core.v1", "rpc.v1", "sdk-baseline-1-1.v1", "views.v1"], webViews);
        File.WriteAllText(Path.Combine(webFolder, "manifest", "sunder-package.json"), JsonSerializer.Serialize(new
        {
            archiveFormatVersion = 1, manifestVersion = 1, id = "local.node", name = "Node", version = "1.0.0",
            dependsOn = Array.Empty<object>(), contractBundles = Array.Empty<object>(), usesContracts = Array.Empty<object>(), provides = Array.Empty<object>(),
            targets = new[] { new { role = webTarget.Role, rid = webTarget.Rid, kind = "web", entryPoint = "index.html", targetFramework = "web",
                sdkVersion = "1.1.0", requiredHostCapabilities = webTarget.RequiredHostCapabilities,
                views = new[] { new { viewId = "local.node.main", displayName = "Node", route = "/", defaultPlacement = "middle", showInHotbar = true } } } },
        }));
        var webPackage = CreateActivePackage("local.node") with { Views = [], Icon = null };
        PackageUiSnapshotDescriptor? previousWeb = null;
        for (var generation = 2; generation <= 3; generation++)
        {
            File.WriteAllText(Path.Combine(webFolder, "index.html"), $"<html><body>Node revision {generation}</body></html>");
            var web = RuntimeContractTestData.Snapshot("local.node", PackageSourceKind.Dev, webFolder) with
            {
                SessionGeneration = generation, Target = webTarget, ActivationFingerprint = new((char)('a' + generation), 64),
                PolicySubject = new("local.node", "1.0.0", new((char)('a' + generation), 64)), SourceContentIdentity = new((char)('a' + generation), 64),
            };
            source = source with { SessionGeneration = generation };
            policy.Expected = source;
            policy.AdditionalSources[web.PackageId] = web;
            var stamp = new RuntimePackageStamp(CreateRuntimeSnapshot(package, source, generation).RuntimeInstanceId, generation);
            var snapshot = new RuntimePackageSnapshot(stamp.RuntimeInstanceId, generation, generation, RuntimeBootstrapState.Ready,
                [package, webPackage], [], [], []);
            await host.ApplyPackageGenerationAsync(snapshot, [source, web], null, (_, _, _) =>
            {
                Assert.Equal("Pending", host.GetAppReadiness("local.node", stamp, web.ActivationFingerprint, web.SourceContentIdentity));
                return Task.FromResult<Action?>(null);
            }, default);
            await host.WaitForRetirementsAsync();
            Assert.Same(managedView, host.GetOrCreateView("agent.chat"));
            Assert.Same(managedOwner.TargetLifetime, RetentionGeneration(host).State.GetLoadedPackage("agent")!.TargetLifetime);
            Assert.Single(handler.Opens, open => open.PackageId == "agent");
            Assert.Equal("Activated", host.GetAppReadiness("local.node", stamp, web.ActivationFingerprint, web.SourceContentIdentity));
            if (previousWeb is not null)
                Assert.Equal("Pending", host.GetAppReadiness("local.node", stamp, previousWeb.ActivationFingerprint, previousWeb.SourceContentIdentity));
            var state = new ShellState();
            var shell = new ShellCompositionService().Compose(host.FilterEnabledPackages(snapshot.ActivePackages), state, null, [], [], ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);
            var view = Assert.Single(shell.PackageViews, item => item.ViewId == "local.node.main");
            Assert.True(view.ShowInHotbarByDefault);
            Assert.Null(view.Icon);
            Assert.False(string.IsNullOrWhiteSpace(view.Glyph));
            Assert.DoesNotContain(view.ViewId, state.HiddenHotbarViewIds);
            var webOwner = RetentionGeneration(host).State.GetLoadedPackage("local.node")!;
            await host.ApplyPackageDeltaAsync([package, webPackage], [source, web]);
            await host.WaitForRetirementsAsync();
            Assert.Same(webOwner.TargetLifetime, RetentionGeneration(host).State.GetLoadedPackage("local.node")!.TargetLifetime);
            previousWeb = web;
        }
        source = source with { SessionGeneration = 4 };
        policy.Expected = source;
        await host.ApplyPackageDeltaAsync([package], [source]);
        await host.WaitForRetirementsAsync();
        Assert.DoesNotContain(host.FilterEnabledPackages([package]).SelectMany(item => item.Views), view => view.ViewId == "local.node.main");
        Assert.Same(managedView, host.GetOrCreateView("agent.chat"));
        Assert.False(GetIsDisposed(managedView!));
        Assert.Equal(2, handler.ClosedSessions.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task BuilderActivation_LoadReloadUnloadKeepsProviderAndSessionAliveUntilActualOwnerRetirement(bool initialQuarantinedSibling, bool overridesInstalled)
    {
        using var temporary = TemporaryDirectoryScope.Create("app-development-retention");
        var builderFolder = CreateAppPackageSource(Path.Combine(temporary.RootPath, "builder"), "agent");
        var projectFolder = CreateAppPackageSource(Path.Combine(temporary.RootPath, "project"), "project");
        File.WriteAllText(Path.Combine(projectFolder, ShellLifecycleTestPackageModule.SkipViewMarkerFileName), "");
        var builder = CreateActiveAgentPackage();
        var project = CreateActivePackage("project") with { Views = [] };
        var runtimeRoot = Path.Combine(temporary.RootPath, "runtime-state");
        var builderSource = await InstalledSnapshotAsync(builderFolder, runtimeRoot);
        var projectSource = RuntimeContractTestData.Snapshot("project", PackageSourceKind.Dev, projectFolder) with { ActivationFingerprint = new('b', 64) };
        var installedProjectSource = overridesInstalled ? await InstalledSnapshotAsync(projectFolder, runtimeRoot, "project") : null;
        var backend = new RetentionBackend { Installed = overridesInstalled };
        await using var host = new PackageViewHostService(new AppPackageViewRegistry(), [], [], [], Path.Combine(temporary.RootPath, "session"),
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync, uiDispatcher: TestUiDispatcher,
            packagePolicy: TestAppPackagePolicy.Instance, developerServices: backend, runtimeStateRootPath: runtimeRoot);
        var generation = 0L;
        var projectLoaded = false;
        var stabilizeBuilder = false;
        var stagedViews = 0;
        async Task Publish()
        {
            var packages = projectLoaded || overridesInstalled ? new[] { builder, project } : [builder];
            var sources = (projectLoaded ? new[] { builderSource, projectSource }
                    : overridesInstalled ? [builderSource, installedProjectSource!] : [builderSource])
                .Select(source => source with { SessionGeneration = generation + 1 }).ToArray();
            if (initialQuarantinedSibling && generation == 0)
            {
                packages = [.. packages, CreateActivePackage("broken")];
                sources = [.. sources, RuntimeContractTestData.Snapshot("broken", PackageSourceKind.Dev, "invalid archive")];
            }
            backend.Snapshot = new(backend.RuntimeId, ++generation, generation, RuntimeBootstrapState.Ready, packages,
                packages.Select(package => new SessionPackageDescriptor(package.PackageId, package.DisplayName, package.Version, package.HostRoles,
                    null, true, package.Readiness, package.Views, null, null, null, 0)).ToArray(), [], []);
            await host.ApplyPackageGenerationAsync(backend.Snapshot, sources, null, async (candidate, _, token) =>
            {
                if (stabilizeBuilder) await candidate.StabilizeViewAsync("agent.chat", stageView: _ => stagedViews++, cancellationToken: token);
                return () => Assert.Equal("Pending", host.GetAppReadiness("agent", new(backend.RuntimeId, generation)));
            }, default);
        }
        backend.Acquire = async () => { if (!projectLoaded) { projectLoaded = true; await Publish(); } };
        backend.Release = async () => { projectLoaded = false; await Publish(); };
        backend.AppStatus = (id, stamp) => host.GetAppReadiness(id, stamp);
        await Publish();
        var originalView = host.GetOrCreateView("agent.chat");
        Assert.NotNull(originalView);
        var provider = (IServiceProvider)originalView.GetType().GetProperty("ServiceProvider")!.GetValue(originalView)!;
        var development = provider.GetRequiredService<IPackageDevelopmentService>();
        var projectionFolder = provider.GetRequiredService<IPackageContext>().ContentRootPath;
        await host.PreloadViewAsync("agent.chat", host.CurrentGenerationId, default);
        stabilizeBuilder = true;
        var workStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.GetRequiredService<IBackgroundTaskQueue>().Enqueue(new("Builder work", "retention-test", BackgroundTaskIndicator.Hidden,
            BackgroundTaskConcurrencyMode.ParallelWithinGroup, true, async context =>
            {
                workStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken); }
                finally { workCancelled.TrySetResult(); }
            }));
        await workStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var session = await development.LoadAsync(projectFolder, false);
        await host.WaitForRetirementsAsync();
        Assert.Same(originalView, host.GetOrCreateView("agent.chat"));
        Assert.False(GetIsDisposed(originalView));
        Assert.True(Directory.Exists(projectionFolder));
        var loaded = await session.GetStatusAsync();
        Assert.True(loaded.IsLoaded);
        Assert.Equal(overridesInstalled, loaded.OverridesInstalledPackage);
        Assert.True(loaded.WatchEnabled); // Another owner's watch is effective.
        Assert.False(loaded.RequestedWatch);
        Assert.Equal(PackageDevelopmentAppReadiness.Activated, loaded.AppReadiness);
        Assert.Equal("Pending", host.GetAppReadiness("project", new(backend.RuntimeId, generation), new string('f', 64)));
        Assert.Equal(0, backend.Releases);
        Assert.Equal(0, stagedViews);
        Assert.False(workCancelled.Task.IsCompleted);

        File.WriteAllText(Path.Combine(projectFolder, "rebuilt"), "new project bytes");
        projectSource = RuntimeContractTestData.Snapshot("project", PackageSourceKind.Dev, projectFolder) with { ActivationFingerprint = new('c', 64) };
        await Publish();
        await host.WaitForRetirementsAsync();
        await session.SetWatchAsync(true);
        Assert.Same(originalView, host.GetOrCreateView("agent.chat"));
        Assert.True((await session.GetStatusAsync()).RequestedWatch);
        Assert.Equal(0, backend.Releases);

        await session.DisposeAsync();
        await host.WaitForRetirementsAsync();
        Assert.Equal(1, backend.Releases);
        Assert.Same(originalView, host.GetOrCreateView("agent.chat"));
        Assert.Equal(0, stagedViews);
        Assert.False(workCancelled.Task.IsCompleted);
        var second = await development.LoadAsync(projectFolder, true);
        // A dependency/source fingerprint change actually retires the Builder owner.
        backend.Release = () => Task.CompletedTask;
        builderSource = builderSource with { ActivationFingerprint = new('d', 64) };
        await Publish();
        await host.WaitForRetirementsAsync();
        Assert.True(GetIsDisposed(originalView));
        Assert.Equal(2, backend.Releases);
        Assert.Equal(1, stagedViews);
        Assert.True(workCancelled.Task.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => second.GetStatusAsync());
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("source")]
    [InlineData("fingerprint")]
    [InlineData("retry")]
    [InlineData("policy")]
    public async Task RetainedActivation_RejectsUnsafeReuseAndSurvivesCandidateRollback(string change)
    {
        using var temporary = TemporaryDirectoryScope.Create("app-retention-fences");
        var folder = CreateAppPackageSource(temporary.RootPath, "agent");
        var package = CreateActiveAgentPackage();
        var runtimeRoot = Path.Combine(temporary.RootPath, "runtime-state");
        var source = (await InstalledSnapshotAsync(folder, runtimeRoot)) with { PolicySubject = new("agent", "1.0.0", new('e', 64)) };
        await using var host = new PackageViewHostService(new AppPackageViewRegistry(), [], [], [], Path.Combine(temporary.RootPath, "session"),
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync, uiDispatcher: TestUiDispatcher,
            packagePolicy: TestAppPackagePolicy.Instance, runtimeStateRootPath: runtimeRoot);
        await host.ApplyPackageDeltaAsync([package], [source]);
        var original = host.GetOrCreateView("agent.chat");
        Assert.NotNull(original);
        var next = change switch
        {
            "source" => RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, folder) with { ActivationFingerprint = source.ActivationFingerprint },
            "fingerprint" => source with { ActivationFingerprint = new('b', 64) },
            "policy" => source with { PolicySubject = source.PolicySubject! with { PackageContentIdentity = new('f', 64) } },
            _ => source,
        };
        if (change == "rejected")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.LifecycleCoordinator.ApplyPackageSnapshotAsync(
                CreateRuntimeSnapshot(package, next, 2), [next with { SessionGeneration = 2 }],
                commitPresentation: _ => throw new InvalidOperationException("presentation rejected")));
        }
        else await host.ApplyPackageDeltaAsync([package], [next], change == "retry" ? ["agent"] : null);
        await host.WaitForRetirementsAsync();
        Assert.Equal(change != "rejected", GetIsDisposed(original));
        Assert.Equal(change == "rejected", ReferenceEquals(original, host.GetOrCreateView("agent.chat")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcknowledgedOwnerWithdrawalRetiresExactActivationAndFencesPendingPublication(bool pendingPublication)
    {
        using var temporary = TemporaryDirectoryScope.Create("app-owner-withdrawal");
        var folder = CreateAppPackageSource(temporary.RootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, folder) with
        {
            ActivationFingerprint = new('a', 64), PolicySubject = new("agent", "1.0.0", new('b', 64)),
        };
        var policy = new ExactRetentionPolicy(source);
        using var handler = new RetentionRpcHandler(policy);
        using var transport = new RuntimeClientTransport(() => new(new Uri("http://runtime.test/"), "test-token"), handler);
        await using var host = new PackageViewHostService(new AppPackageViewRegistry(), [], [], [], temporary.GetPath("session"),
            getRuntimeConnectionInfo: transport.GetConnectionInfo, uiDispatcher: TestUiDispatcher, packagePolicy: policy,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);
        await host.ApplyPackageDeltaAsync([package], [source]);
        var view = host.GetOrCreateView("agent.chat");
        Assert.NotNull(view);
        var admission = RetentionGeneration(host).State.GetLoadedPackage("agent")!.LoadAdmission!;
        var requirement = new RuntimeAppActivationRetirementRequirement("test-policy", admission.ActivationId, admission.PermitId!, source.PolicySubject!);
        await host.RetireActivationsAsync([requirement with { PolicySessionId = "foreign-app" }], default);
        Assert.False(GetIsDisposed(view));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? publication = null;
        if (pendingPublication)
        {
            var next = source with { SessionGeneration = 2, SnapshotId = Guid.NewGuid().ToString("N") };
            policy.Expected = next;
            publication = host.ApplyPackageGenerationAsync(CreateRuntimeSnapshot(package, next, 2), [next], null,
                async (_, _, _) => { entered.TrySetResult(); await release.Task; return null; }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        policy.BeforeRetire = () => Assert.Contains(handler.SessionIds.First(), handler.ClosedSessions);
        try
        {
            await host.RetireActivationsAsync([requirement], default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(GetIsDisposed(view));
            Assert.Equal(0, host.LoadedPackageCount);
            Assert.Equal(admission.ActivationId, Assert.Single(policy.RetiredActivationIds));
            await host.RetireActivationsAsync([requirement], default); // Exact acknowledgement is idempotent.
        }
        finally { release.TrySetResult(); }
        if (publication is not null) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication);
        await host.WaitForRetirementsAsync();
        Assert.Equal(0, host.RetirementFailureCount);
    }

    [Fact]
    public async Task NaturalGenerationRetirementJoinsAlreadyRemovedActivationDrain()
    {
        using var temporary = TemporaryDirectoryScope.Create("app-retirement-drain-join");
        var folder = CreateAppPackageSource(temporary.RootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, folder);
        await using var host = new PackageViewHostService(new AppPackageViewRegistry(), [], [], [], temporary.GetPath("session"),
            uiDispatcher: TestUiDispatcher, packagePolicy: TestAppPackagePolicy.Instance,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);
        await host.ApplyPackageDeltaAsync([package], [source]);
        var presentation = RetentionGeneration(host);
        var group = Assert.Single(presentation.ActivationGroups, item => item.OwnedActivationIds.Contains("agent"));
        var handle = group.State.GetLoadedPackage("agent")!;
        var target = new BlockedRetirementLifetime(handle.TargetLifetime);
        group.State.SetLoadedPackage("agent", handle with { TargetLifetime = target });
        var retirement = group.Composition.RetireForOwnerReleaseAsync("agent", default);
        await target.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task? naturalDrain = null;
        try
        {
            await host.ApplyPackageDeltaAsync([], []);
            naturalDrain = host.WaitForRetirementsAsync();
            await Eventually.AssertPendingAsync(naturalDrain, () => "Generation resources were released before the exact activation drain.");
            Assert.True(Directory.Exists(presentation.Folder));
            Assert.False(retirement.IsCompleted);
        }
        finally { target.Release.TrySetResult(); }
        await retirement.WaitAsync(TimeSpan.FromSeconds(3));
        if (naturalDrain is not null) await naturalDrain.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, target.Disposals);
        Assert.Equal(0, host.RetirementFailureCount);
    }

    [Fact]
    public async Task FailedHostRetirementAcknowledgementRetriesExactAdmissionWithoutRepeatingLocalDisposal()
    {
        using var temporary = TemporaryDirectoryScope.Create("app-retirement-ack-retry");
        var folder = CreateAppPackageSource(temporary.RootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, folder) with { PolicySubject = new("agent", "1.0.0", new('b', 64)) };
        var policy = new ExactRetentionPolicy(source);
        using var handler = new RetentionRpcHandler(policy);
        using var transport = new RuntimeClientTransport(() => new(new Uri("http://runtime.test/"), "test-token"), handler);
        await using var host = new PackageViewHostService(new AppPackageViewRegistry(), [], [], [], temporary.GetPath("session"),
            getRuntimeConnectionInfo: transport.GetConnectionInfo, uiDispatcher: TestUiDispatcher, packagePolicy: policy,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);
        await host.ApplyPackageDeltaAsync([package], [source]);
        var admission = RetentionGeneration(host).State.GetLoadedPackage("agent")!.LoadAdmission!;
        var requirement = new RuntimeAppActivationRetirementRequirement("test-policy", admission.ActivationId, admission.PermitId!, source.PolicySubject!);
        var attempts = 0;
        policy.BeforeRetire = () => { if (++attempts == 1) throw new IOException("Transient policy transport failure."); };
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RetireActivationsAsync([requirement], default));
        Assert.Single(handler.ClosedSessions);
        Assert.Empty(policy.RetiredActivationIds);
        await host.RetireActivationsAsync([requirement], default);
        Assert.Equal(2, attempts);
        Assert.Single(handler.ClosedSessions);
        Assert.Equal(admission.ActivationId, Assert.Single(policy.RetiredActivationIds));
        await host.ApplyPackageDeltaAsync([], []);
        await host.WaitForRetirementsAsync();
        Assert.Equal(0, host.RetirementFailureCount);
    }

    [Fact]
    public async Task CandidateActivationRetirementDoesNotDetachPublishedOwnersBoundary()
    {
        using var temporary = TemporaryDirectoryScope.Create("app-retirement-boundary-owner");
        var folder = CreateAppPackageSource(temporary.RootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, folder) with
        {
            ActivationFingerprint = new('a', 64), PolicySubject = new("agent", "1.0.0", new('b', 64)),
        };
        var policy = new ExactRetentionPolicy(source);
        using var handler = new RetentionRpcHandler(policy);
        using var transport = new RuntimeClientTransport(() => new(new Uri("http://runtime.test/"), "test-token"), handler);
        await using var host = new PackageViewHostService(new AppPackageViewRegistry(), [], [], [], temporary.GetPath("session"),
            getRuntimeConnectionInfo: transport.GetConnectionInfo, uiDispatcher: TestUiDispatcher, packagePolicy: policy,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);
        await host.ApplyPackageDeltaAsync([package], [source]);
        var view = host.GetOrCreateView("agent.chat");
        var boundary = Assert.IsType<HostedPackageViewBoundary>(host.CreateHostedViewBoundary("agent", "agent.chat", view));
        var publishedOwner = boundary.ActivationOwnerId;
        var next = source with { SessionGeneration = 2, SnapshotId = Guid.NewGuid().ToString("N"), ActivationFingerprint = new('c', 64) };
        policy.Expected = next;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ApplyPackageGenerationAsync(CreateRuntimeSnapshot(package, next, 2), [next], null,
            async (_, _, token) =>
            {
                var caller = handler.Opens.Last();
                Assert.NotEqual(publishedOwner, caller.AppGenerationId);
                await host.RetireActivationsAsync([new("test-policy", caller.AppGenerationId, caller.LoadPermitId!, next.PolicySubject!)], token);
                Assert.Same(view, boundary.HostedView);
                Assert.False(GetIsDisposed(view!));
                return null;
            }, default));
        await host.WaitForRetirementsAsync();
        Assert.Same(view, boundary.HostedView);
        Assert.Same(view, host.GetOrCreateView("agent.chat"));
        boundary.Release();
        Assert.Equal(0, host.RetirementFailureCount);
    }

    private sealed class BlockedRetirementLifetime(IAsyncDisposable? inner) : IAsyncDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposals;
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            Entered.TrySetResult();
            await Release.Task;
            if (inner is not null) await inner.DisposeAsync();
        }
    }

    private static AppPackageGeneration RetentionGeneration(PackageViewHostService host)
        => (AppPackageGeneration)typeof(PackageViewHostService)
            .GetProperty("CurrentGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

    private sealed class ExactRetentionPolicy(PackageUiSnapshotDescriptor initial) : IAppPackagePolicy
    {
        internal PackageUiSnapshotDescriptor Expected = initial;
        internal Dictionary<string, PackageUiSnapshotDescriptor> AdditionalSources { get; } = new(StringComparer.Ordinal);
        internal PackageUiSnapshotDescriptor ExpectedFor(string id) => AdditionalSources.GetValueOrDefault(id) ?? Expected;
        internal bool Deny;
        internal ConcurrentQueue<PackageUiSnapshotDescriptor> Requests { get; } = new();
        internal ConcurrentDictionary<string, (Guid ActivationId, PackageUiSnapshotDescriptor Source)> Admissions { get; } = new();
        internal ConcurrentQueue<Guid> RetiredActivationIds { get; } = new();
        internal Action? BeforeRetire;
        public bool OwnsRetirement(RuntimeAppActivationRetirementRequirement requirement) => requirement.PolicySessionId == "test-policy";
        public bool HasRetiredActivation(RuntimeAppActivationRetirementRequirement requirement)
            => OwnsRetirement(requirement) && RetiredActivationIds.Contains(requirement.AppActivationId);
        public async Task<AppPackageLoadAdmission> AcquireLoadAsync(PackageUiSnapshotDescriptor snapshot, Guid activationId, CancellationToken token)
        {
            await ValidateAsync(snapshot, token);
            var permit = Guid.NewGuid().ToString("N");
            Assert.True(Admissions.TryAdd(permit, (activationId, snapshot)));
            return new(activationId, permit, _ =>
            {
                BeforeRetire?.Invoke();
                RetiredActivationIds.Enqueue(activationId);
                return Task.CompletedTask;
            }) { PolicySessionId = "test-policy" };
        }
        public Task RequireLoadAsync(PackageUiSnapshotDescriptor snapshot, CancellationToken token)
        {
            if (AppProcessPolicySupervisor.GetImplementedObservationCapabilities(snapshot.Target) is not null)
                throw new InvalidOperationException("Update the App to implement the selected managed App capabilities.");
            return ValidateAsync(snapshot, token);
        }
        public Task RequireRetainedLoadAsync(PackageUiSnapshotDescriptor snapshot, AppPackageLoadAdmission admission, CancellationToken token)
        {
            Assert.Equal("test-policy", admission.PolicySessionId);
            Assert.True(Admissions.TryGetValue(admission.PermitId!, out var previous));
            Assert.Equal(previous.ActivationId, admission.ActivationId);
            Assert.Equal(previous.Source.PolicySubject!.PackageId, snapshot.PolicySubject!.PackageId);
            Assert.Equal(previous.Source.Target, snapshot.Target);
            return ValidateAsync(snapshot, token);
        }
        private Task ValidateAsync(PackageUiSnapshotDescriptor snapshot, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Same(ExpectedFor(snapshot.PackageId), snapshot); // Exact full policy, snapshot ID and new generation, never a role digest.
            Assert.NotNull(snapshot.PolicySubject);
            Requests.Enqueue(snapshot);
            return Deny ? Task.FromException(new InvalidOperationException("new source permit denied")) : Task.CompletedTask;
        }
    }

    private sealed class RetentionRpcHandler(ExactRetentionPolicy policy) : HttpMessageHandler
    {
        internal bool FailOpen;
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? DataRequest;
        internal ConcurrentQueue<RuntimeRpcAppSessionOpenRequest> Opens { get; } = new();
        internal ConcurrentQueue<string> SessionIds { get; } = new();
        internal ConcurrentQueue<string> ClosedSessions { get; } = new();
        internal ConcurrentQueue<RuntimeRpcAppInvariantViolationRequest> Reports { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/data/", StringComparison.Ordinal) && DataRequest is { } dataRequest)
                return await dataRequest(request, token);
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/handshake":
                    return Json(new RuntimeHandshakeResponse(RuntimeProtocol.Identity, 1, 1, 1,
                        Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.OfficialRuntimeDownloadsV1,
                            RuntimeProtocolFeatures.AppWebRpcV1, RuntimeProtocolFeatures.SchemaFirstRpcV1], new("Sunder.Runtime.Host", "1.0.0", "1.0.0")));
                case "/api/v1/rpc/app-sessions/open":
                    var opened = (await request.Content!.ReadFromJsonAsync<RuntimeRpcAppSessionOpenRequest>(token))!;
                    var expected = policy.ExpectedFor(opened.PackageId);
                    Assert.Contains(policy.Requests, source => ReferenceEquals(source, expected));
                    Assert.False(policy.Deny);
                    Assert.Equal(expected.SessionGeneration, opened.SessionGeneration);
                    Assert.Equal(expected.PolicySubject!.PackageId, opened.PackageId);
                    Assert.Equal(expected.PolicySubject.Version, opened.PackageVersion);
                    Assert.NotNull(opened.LoadPermitId);
                    Assert.True(policy.Admissions.TryGetValue(opened.LoadPermitId, out var admission));
                    Assert.Equal(opened.AppGenerationId, admission.ActivationId);
                    Assert.Same(expected, admission.Source);
                    Opens.Enqueue(opened);
                    if (FailOpen) return new(HttpStatusCode.ServiceUnavailable);
                    var id = Guid.NewGuid().ToString("N");
                    SessionIds.Enqueue(id);
                    return Json(new RuntimeRpcAppSessionDescriptor(id));
                case "/api/v1/rpc/app-sessions/close":
                    ClosedSessions.Enqueue((await request.Content!.ReadFromJsonAsync<RuntimeRpcAppSessionCloseRequest>(token))!.SessionId);
                    return new(HttpStatusCode.NoContent);
                case "/api/v1/rpc/app/call-scopes/open":
                    return Json(new RuntimeRpcAppCallScopeDescriptor(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(1)));
                case "/api/v1/rpc/app/call-scopes/close":
                    return new(HttpStatusCode.NoContent);
                case "/api/v1/rpc/app/invariant-violation":
                    var report = (await request.Content!.ReadFromJsonAsync<RuntimeRpcAppInvariantViolationRequest>(token))!;
                    Assert.DoesNotContain(report.SessionId, ClosedSessions);
                    Reports.Enqueue(report);
                    return Json(new RuntimeRpcAppInvariantViolationResponse(true, null));
                default: throw new InvalidOperationException($"Unexpected retention RPC route: {request.RequestUri.AbsolutePath}");
            }
        }
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }

    private static async Task<PackageUiSnapshotDescriptor> InstalledSnapshotAsync(string folder, string runtimeRoot, string packageId = "agent")
    {
        var snapshot = RuntimeContractTestData.Snapshot(packageId, PackageSourceKind.Dev, folder);
        var files = await AppPackageContentIndexer.IndexAsync(folder, default);
        var cache = new PackageProjectionCache(runtimeRoot);
        await using var projection = await cache.GetOrBuildAndLeaseAsync(new PackageProjectionRequest(folder, packageId, snapshot.ContentHash,
            PackageProjectionRole.App, PackageProjectionCache.DefaultRulesIdentity, [$"app/{snapshot.Target.Rid}"],
            files.Select(file => new PackageProjectionSourceFile(file.Path, file.Path, file.Sha256, file.Length)).ToArray(),
            ["manifest/sunder-package.json", snapshot.Target.EntryPoint]), new PackageProjectionOwner("test-app-retention", Guid.NewGuid(), packageId, "1"));
        var reference = projection.Reference;
        return snapshot with
        {
            SourceKind = PackageSourceKind.Installed, ContentHash = reference.CacheKey, ActivationFingerprint = new('a', 64), SnapshotUri = "",
            ProjectionReference = new(reference.FormatRevision, reference.CacheKey, "app", reference.SourceContentIdentity,
                reference.ProjectionRulesIdentity, reference.TargetKeys),
        };
    }

    private sealed class RetentionBackend : IAppPackageDevelopmentBackend
    {
        internal Guid RuntimeId { get; } = Guid.NewGuid();
        internal RuntimePackageSnapshot Snapshot { get; set; } = null!;
        internal Func<Task> Acquire { get; set; } = () => Task.CompletedTask;
        internal Func<Task> Release { get; set; } = () => Task.CompletedTask;
        internal Func<string, RuntimePackageStamp, string> AppStatus { get; set; } = (_, _) => "Pending";
        internal int Releases;
        internal bool Installed { get; init; }
        public IDevPackageOwnerSession CreateOwner(string ownerPrefix) => new Owner(this);
        public Task<string> PrepareAsync(string folder, CancellationToken token) => Task.FromResult("project");
        public Task<RuntimePackageSnapshot> GetSnapshotAsync(CancellationToken token) => Task.FromResult(Snapshot);
        public Task<bool> IsInstalledAsync(string packageId, CancellationToken token) => Task.FromResult(Installed);
        public Task<string> GetAppReadinessAsync(string packageId, RuntimePackageStamp stamp, CancellationToken token) => Task.FromResult(AppStatus(packageId, stamp));
        public Task OpenLogsAsync(string packageId, AppPackageGenerationPublication publication, CancellationToken token) => Task.CompletedTask;
        private sealed class Owner(RetentionBackend backend) : IDevPackageOwnerSession
        {
            public async Task<DevPackageOwnerLeaseResponse> AcquireAsync(IReadOnlyList<string> folders, bool watch, CancellationToken cancellationToken = default)
            {
                await backend.Acquire();
                cancellationToken.ThrowIfCancellationRequested();
                return new(backend.RuntimeId, "owner", "mutation", 1, DateTimeOffset.UtcNow.AddMinutes(1), backend.Snapshot.SessionGeneration,
                    [new("project", watch) { EffectiveWatch = true }]);
            }
            public Task ReleaseAsync(CancellationToken cancellationToken = default) => backend.Release();
            public async ValueTask DisposeAsync() { backend.Releases++; await backend.Release(); }
        }
    }
}
