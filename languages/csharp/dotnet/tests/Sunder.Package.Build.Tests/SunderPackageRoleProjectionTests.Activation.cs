using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Build.Authoring;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderPackageRoleProjectionTests
{
    [Theory]
    [InlineData("candidate")]
    [InlineData("activate")]
    [InlineData("cancel")]
    [InlineData("fail")]
    public async Task GeneratedActivation_PreservesDependencyOrderCancellationAndCandidateCleanup(string mode)
    {
        var analysis = SunderPackageCompositionPlannerTests.Analyze("""
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Worker;
            namespace Example;
            public static class Journal
            {
                public static List<string> Events { get; } = new();
                public static string Mode { get; set; } = "activate";
                public static TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            public sealed class ZDependency : ISunderWorkerActivationService, IAsyncDisposable
            {
                public async ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken token = default)
                {
                    Journal.Events.Add("activate:dependency");
                    Journal.Started.TrySetResult();
                    if (Journal.Mode == "cancel") await Task.Delay(Timeout.Infinite, token);
                    if (Journal.Mode == "fail") throw new InvalidOperationException("Activation failed.");
                }
                public ValueTask DisposeAsync() { Journal.Events.Add("dispose:dependency"); return ValueTask.CompletedTask; }
            }
            public sealed class AStarter : ISunderWorkerActivationService, IDisposable
            {
                public AStarter(ZDependency dependency) { }
                public ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken token = default)
                { token.ThrowIfCancellationRequested(); Journal.Events.Add("activate:starter"); return ValueTask.CompletedTask; }
                public void Dispose() => Journal.Events.Add("dispose:starter");
            }
            """);
        Assert.True(analysis.Success, Errors(analysis));
        Assert.All(analysis.RolePlan.Types, type => Assert.Equal(SunderPackageRoleMembership.Worker, type.Roles));
        Assert.Equal(new[] { "ZDependency", "AStarter" }, analysis.Composition!.Worker.Where(entry => entry.Activation).Select(entry => entry.Type.Name));
        Assert.DoesNotContain(analysis.Composition.Worker, entry => entry.Lifecycle);
        var load = new AssemblyLoadContext("generated-activation-" + mode, isCollectible: true);
        try
        {
            var worker = EmitCompositionRole(load, analysis, SunderPackageRoleMembership.Worker);
            var configured = Configure(worker, DispatchProxy.Create<ISunderRpcClient, MetadataOnlyContext>());
            Assert.Null(configured.Composition);
            Assert.NotNull(configured.OnActivated);
            var shutdown = configured.OnShutdown!;
            var services = (IServiceProvider)shutdown.Target!.GetType().GetProperty("Services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shutdown.Target)!;
            var journal = worker.GetType("Example.Journal", true)!;
            var events = (List<string>)journal.GetProperty("Events")!.GetValue(null)!;
            journal.GetProperty("Mode")!.SetValue(null, mode);
            Assert.Empty(events);
            try
            {
                // Materialize both objects, as a pre-activation provider request can,
                // without invoking activation or starting package-owned work.
                Assert.NotNull(services.GetService(worker.GetType("Example.AStarter", true)!));
                Assert.Empty(events);
                if (mode != "candidate")
                {
                    using var cancellation = new CancellationTokenSource();
                    var context = (SunderWorkerContext)RuntimeHelpers.GetUninitializedObject(typeof(SunderWorkerContext));
                    var activation = configured.OnActivated(context, cancellation.Token).AsTask();
                    if (mode == "cancel")
                    {
                        await ((TaskCompletionSource)journal.GetProperty("Started")!.GetValue(null)!).Task.WaitAsync(TimeSpan.FromSeconds(5));
                        cancellation.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation);
                    }
                    else if (mode == "fail")
                        Assert.Equal("Activation failed.", (await Assert.ThrowsAsync<InvalidOperationException>(() => activation)).Message);
                    else await activation;
                    Assert.Equal(mode == "activate" ? new[] { "activate:dependency", "activate:starter" } : ["activate:dependency"], events);
                }
            }
            finally { await shutdown(ShutdownReason(), CancellationToken.None); }
            await shutdown(ShutdownReason(), CancellationToken.None);
            Assert.Equal(new[] { "dispose:starter", "dispose:dependency" }, events.Where(value => value.StartsWith("dispose:", StringComparison.Ordinal)));
        }
        finally { load.Unload(); }
    }
}
