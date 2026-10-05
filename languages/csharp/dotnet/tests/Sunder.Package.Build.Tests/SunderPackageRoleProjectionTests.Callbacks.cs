using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Build.Authoring;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderPackageRoleProjectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedCallbacks_PreserveSdkRoutingAndOwnServicesOnConstructionFailure(bool failConstruction)
    {
        var analysis = SunderPackageCompositionPlannerTests.Analyze(CallbackSource);
        Assert.True(analysis.Success, Errors(analysis));
        Assert.All(analysis.RolePlan.Types, type => Assert.Equal(SunderPackageRoleMembership.Worker, type.Roles));
        var load = new AssemblyLoadContext("generated-auth-callbacks", isCollectible: true);
        try
        {
            var worker = EmitCompositionRole(load, analysis, SunderPackageRoleMembership.Worker);
            var journal = worker.GetType("Example.Journal", true)!;
            var events = (List<string>)journal.GetProperty("Events")!.GetValue(null)!;
            journal.GetProperty("FailConstruction")!.SetValue(null, failConstruction);
            var rpc = DispatchProxy.Create<ISunderRpcClient, MetadataOnlyContext>();
            if (failConstruction)
            {
                var failure = Assert.Throws<TargetInvocationException>(() => Configure(worker, rpc));
                Assert.Equal("Callback construction failed.", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
            }
            else
            {
                var options = Configure(worker, rpc);
                await using var composition = options.Composition!;
                var auth = Assert.IsAssignableFrom<IPackageAuthHandler>(options.AuthHandler);
                var callback = Assert.Single(options.CallbackHandlers);
                Assert.Same(auth, composition.Services.GetRequiredService(worker.GetType("Example.Auth", true)!));
                Assert.Same(callback, composition.Services.GetRequiredService(worker.GetType("Example.Callback", true)!));
                Assert.Equal(PackageCallbackHandlerIds.Authentication, ((IPackageCallbackHandler)auth).CallbackHandlerId);
                Assert.Equal("example.callback", callback.CallbackHandlerId);
                Assert.DoesNotContain(options.CallbackHandlers, item => ReferenceEquals(item, auth));
                Assert.Equal(new[] { "ctor:shared", "ctor:auth", "ctor:callback" }, events);
                await Stage(composition, "PrepareCandidateAsync", CancellationToken.None);
                var generation = (SunderWorkerGenerationContext)Activator.CreateInstance(typeof(SunderWorkerGenerationContext), BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null, args: [Guid.NewGuid(), 1L], culture: null)!;
                await Stage(composition, "CommitGenerationAsync", generation, CancellationToken.None);
                Assert.DoesNotContain("activate:callback", events);
                await Stage(composition, "ActivateAsync", RuntimeHelpers.GetUninitializedObject(typeof(SunderWorkerContext)), CancellationToken.None);
                Assert.Single(events, value => value == "activate:callback");
                Assert.Equal(PackageAuthStatusKind.NotConnected, (await auth.GetStatusAsync()).Status);
                var start = new PackageCallbackStartContext("session", new Uri("https://callback.example/complete"));
                Assert.Equal("session", (await ((IPackageCallbackHandler)auth).StartCallbackAsync(start))!.CallbackSessionId);
                Assert.Equal("session", (await callback.StartCallbackAsync(start))!.CallbackSessionId);
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callback.StartCallbackAsync(start, cancelled.Token));
                await ((IPackageCallbackHandler)auth).CancelCallbackAsync(new("session", PackageCallbackCancellationReason.CallerRequested));
                Assert.Contains("cancel:auth:session", events);
                await composition.DisposeAsync();
                await composition.DisposeAsync();
                Assert.Single(events, value => value == "dispose:callback");
            }
            Assert.Single(events, value => value == "dispose:auth");
            Assert.Single(events, value => value == "dispose:shared");
            if (failConstruction) Assert.DoesNotContain("dispose:callback", events);
        }
        finally { load.Unload(); }
    }

    [Fact]
    public void CallbackRoots_RejectDuplicateAuthAndAppPlacement()
    {
        var duplicate = SunderPackageCompositionPlannerTests.Analyze(CallbackSource + "public sealed class OtherAuth(Shared shared) : Auth(shared);");
        Assert.Equal(2, duplicate.RolePlan.Diagnostics.Count(error => error.Id == "package-authoring.auth-handler-duplicate" && error.Path == "composition.cs"));
        var app = SunderPackageCompositionPlannerTests.Analyze(CallbackSource.Replace("public sealed class Callback",
            "[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]\npublic sealed class Callback", StringComparison.Ordinal));
        Assert.Contains(app.RolePlan.Diagnostics, error => error.Id == "package-authoring.role-conflict" && error.Path == "composition.cs");
    }

    private const string CallbackSource = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Sunder.Sdk.Abstractions;
        using Sunder.Sdk.Authentication;
        using Sunder.Sdk.Callbacks;
        namespace Example;
        public static class Journal
        {
            public static List<string> Events { get; } = new();
            public static bool FailConstruction { get; set; }
        }
        public sealed class Shared : IDisposable
        {
            public Shared() => Journal.Events.Add("ctor:shared");
            public void Dispose() => Journal.Events.Add("dispose:shared");
        }
        public class Auth : IPackageAuthHandler, IDisposable
        {
            public Auth(Shared shared) => Journal.Events.Add("ctor:auth");
            public ValueTask<PackageAuthStatus> GetStatusAsync(CancellationToken token = default)
            { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new PackageAuthStatus("example.notes", PackageAuthStatusKind.NotConnected, "Not connected.", true, false)); }
            public Task<PackageAuthSessionStartResult?> StartAuthorizationAsync(PackageAuthSessionStartContext context, CancellationToken token = default)
            { token.ThrowIfCancellationRequested(); return Task.FromResult<PackageAuthSessionStartResult?>(new("example.notes", context.AuthSessionId, PackageAuthFlowKind.Browser, context.CallbackUri.AbsoluteUri, "Start")); }
            public Task<PackageAuthStatus> CompleteAuthorizationAsync(PackageAuthSessionCompletionContext context, CancellationToken token = default) => GetStatusAsync(token).AsTask();
            public Task<PackageAuthStatus> DisconnectAsync(CancellationToken token = default) => GetStatusAsync(token).AsTask();
            public Task CancelCallbackAsync(PackageCallbackCancellationContext context, CancellationToken token = default)
            { token.ThrowIfCancellationRequested(); Journal.Events.Add("cancel:auth:" + context.CallbackSessionId); return Task.CompletedTask; }
            public void Dispose() => Journal.Events.Add("dispose:auth");
        }
        public sealed class Callback : IPackageCallbackHandler, Sunder.Sdk.Worker.ISunderWorkerActivationService, IDisposable
        {
            public Callback(Shared shared)
            {
                if (Journal.FailConstruction) throw new InvalidOperationException("Callback construction failed.");
                Journal.Events.Add("ctor:callback");
            }
            public string CallbackHandlerId => "example.callback";
            public ValueTask ActivateAsync(Sunder.Sdk.Worker.SunderWorkerContext context, CancellationToken token = default)
            { token.ThrowIfCancellationRequested(); Journal.Events.Add("activate:callback"); return ValueTask.CompletedTask; }
            public Task<PackageCallbackStartResult?> StartCallbackAsync(PackageCallbackStartContext context, CancellationToken token = default)
            { token.ThrowIfCancellationRequested(); return Task.FromResult<PackageCallbackStartResult?>(new("example.notes", context.CallbackSessionId, PackageCallbackFlowKind.Browser, context.CallbackUri.AbsoluteUri, "Start")); }
            public Task<PackageCallbackCompletionResult> CompleteCallbackAsync(PackageCallbackCompletionContext context, CancellationToken token = default)
            { token.ThrowIfCancellationRequested(); return Task.FromResult(new PackageCallbackCompletionResult("example.notes", context.CallbackSessionId, PackageCallbackCompletionState.Completed, "Complete")); }
            public void Dispose() => Journal.Events.Add("dispose:callback");
        }
        """;
}
