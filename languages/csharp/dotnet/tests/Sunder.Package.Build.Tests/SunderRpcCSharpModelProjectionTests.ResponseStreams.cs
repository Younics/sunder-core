using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpModelProjectionTests
{
    private const string ResponseWatchSource = """
        using System;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;
        using System.Threading;
        using System.Threading.Tasks;
        using Sunder.Sdk.Services;
        using Sunder.Sdk.Rpc;
        [SunderService(SunderServiceExposure.Public, ServiceId = "messages")]
        public sealed partial class WatchService
        {
            public bool BodyFailure { get; set; }
            public bool BusinessFailure { get; set; }
            public int Disposals { get; private set; }
            [SunderMethod(CompleteOnResponseContentRetirement = true)]
            public async IAsyncEnumerable<SunderContent> ChangesAsync([SunderValueConstraints(Minimum = "0")] long afterRevision, [EnumeratorCancellation] CancellationToken token = default)
            {
                try
                {
                    for (var revision = afterRevision + 1; revision <= 2; revision++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (BusinessFailure) throw new InvalidOperationException("business-canary");
                        if (BodyFailure) throw new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "rpc.content.authority-revoked", "business-canary"));
                        await using var content = SunderContent.FromBytes(new byte[] { (byte)revision }, "application/json", "subagents-change.json");
                        yield return content;
                    }
                }
                finally { Disposals++; }
            }
            public ValueTask<SunderContent> ReadAsync() => ValueTask.FromResult(SunderContent.FromBytes(new byte[] { 1 }, "application/json", "item.json"));
        }
        """;

    [Theory]
    [InlineData("unavailable", "rpc.content.authority-revoked", "complete", true)]
    [InlineData("deadline-exceeded", "rpc.call.deadline-exceeded", "complete", true)]
    [InlineData("stale-endpoint", "rpc.endpoint.stale", "complete", true)]
    [InlineData("cancelled", "rpc.call.cancelled", "complete", true)]
    [InlineData("unavailable", "rpc.content.authority-revoked", "default", true)]
    [InlineData("unavailable", "rpc.content.authority-revoked", "body", true)]
    [InlineData("unavailable", "rpc.content.authority-revoked", "business", true)]
    [InlineData("unavailable", "rpc.content.authority-revoked", "unary", true)]
    [InlineData("unavailable", "rpc.content.authority-revoked", "unsafe", true)]
    [InlineData("unavailable", "rpc.content.authority-revoked", "dispatched", true)]
    [InlineData("unavailable", "rpc.other.operation", "other", true)]
    [InlineData("permission-denied", "rpc.content.authority-revoked", "mismatch", true)]
    [InlineData("unavailable", "rpc.content.authority-revoked", "default", false)]
    public async Task ResponseRetirement_CompiledWatchUsesActualWorkerCompletionAndFaultBoundary(string kind, string code, string scenario, bool operationalOutcomesV1)
    {
        var source = scenario == "default" ? ResponseWatchSource.Replace("Retirement = true", "Retirement = false", StringComparison.Ordinal) : ResponseWatchSource;
        var result = Compile(source);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var baseline = Compile(ResponseWatchSource.Replace("Retirement = true", "Retirement = false", StringComparison.Ordinal));
        Assert.Equal(Assert.Single(baseline.Contracts).Descriptor.GetCanonicalUtf8(), Assert.Single(result.Contracts).Descriptor.GetCanonicalUtf8());
        Assert.Equal(baseline.Contracts[0].Descriptor.AbiSha256, result.Contracts[0].Descriptor.AbiSha256);
        Assert.Empty(result.Contracts[0].MethodPolicies.Services);
        var sources = new List<SunderRpcCSharpSourceFile>
        {
            new("source.cs", source), new("generated.cs", SunderRpcCSharpImplementationEmitter.Emit(result.Contracts)),
            new("schema.cs", SunderRpcCSharpProjectedBindings.SchemaSource(result.Contracts[0].GeneratedLogicalName, result.Contracts[0].Descriptor)),
            new("globals.cs", "global using System; global using System.IO; global using System.Collections.Generic; global using System.Linq; global using System.Threading; global using System.Threading.Tasks; global using Xunit;"),
            new("probe.cs", ResponseWorkerProbe),
        };
        // Reuse the real Worker test harness under its existing friend identity.
        // No production visibility change or substitute Worker error mapper is used.
        sources.AddRange(WorkerHarnessSources());
        var (load, probe) = LoadProjectionProbe(sources, "ResponseWorkerProbe", "Sunder.Sdk.Worker.Tests");
        try
        {
            var run = probe.GetMethod("Run")!.CreateDelegate<Func<string, string, string, bool, Task>>();
            await run(kind, code, scenario, operationalOutcomesV1).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { load.Unload(); }
    }

    private static IEnumerable<SunderRpcCSharpSourceFile> WorkerHarnessSources([CallerFilePath] string source = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../Sunder.Sdk.Worker.Tests/TestSupport"));
        foreach (var name in new[] { "WorkerHarness.cs", "AsyncByteStream.cs" })
            yield return new(name, File.ReadAllText(Path.Combine(root, name)));
    }

    private const string ResponseWorkerProbe = """
        using System.Text.Json;
        using Sunder.Sdk.Worker;
        using Sunder.Sdk.Worker.Tests.TestSupport;
        public static class ResponseWorkerProbe
        {
            public static async Task Run(string kind, string code, string scenario, bool operationalOutcomesV1)
            {
                var service = new WatchService { BodyFailure = scenario == "body", BusinessFailure = scenario == "business" };
                var handler = service.CreateSunderRpcProviderRegistration().Handler;
                await using var worker = new WorkerHarness(_ => new SunderWorkerOptions([WorkerHarness.Registration(handler)]), operationalOutcomesV1: operationalOutcomesV1);
                await worker.HandshakeAndActivateAsync();
                if (scenario == "unary")
                {
                    await worker.SendInvocationAsync("watch", "unary", "read", new { });
                    await Register(worker, success: false, kind, code, scenario);
                }
                else await worker.SendInvocationAsync("watch", "server-stream", "changes", 0L);
                if (scenario is not ("body" or "business" or "unary"))
                {
                    await Register(worker, success: true, kind, code, scenario);
                    using var first = await worker.ReadAsync();
                    Assert.Equal("worker.event", first.RootElement.GetProperty("type").GetString());
                    await Register(worker, success: false, kind, code, scenario);
                }
                // Authenticated registration unavailability is operational when
                // negotiated, even when it must not silently complete the stream.
                // Forged/body failures and legacy peers still use the fault boundary.
                var providerFault = !operationalOutcomesV1 || scenario is "body" or "business" or "mismatch";
                if (scenario != "complete" && providerFault)
                {
                    using var diagnostic = await worker.ReadAsync();
                    Assert.Equal("worker.provider-fault-diagnostic", diagnostic.RootElement.GetProperty("type").GetString());
                    await worker.SendAsync(new { type = "host.result", id = diagnostic.RootElement.GetProperty("id").GetString(), value = (object?)null });
                }
                using (var terminal = await worker.ReadAsync())
                {
                    Assert.Equal(scenario == "complete" ? "worker.complete" : "worker.error", terminal.RootElement.GetProperty("type").GetString());
                    if (scenario != "complete")
                    {
                        var error = terminal.RootElement.GetProperty("error");
                        Assert.Equal(providerFault ? "provider-fault" : "operational", error.GetProperty("kind").GetString());
                        Assert.Equal(providerFault ? "rpc.provider.handler-fault" : "unavailable", error.GetProperty("code").GetString());
                        Assert.False(error.TryGetProperty("dispatchState", out _));
                        Assert.False(error.TryGetProperty("retrySafety", out _));
                        Assert.False(error.TryGetProperty("details", out _));
                    }
                    Assert.DoesNotContain("business-canary", terminal.RootElement.GetRawText(), StringComparison.Ordinal);
                }
                Assert.Equal(scenario == "unary" ? 0 : 1, service.Disposals);
                service.BodyFailure = false;
                service.BusinessFailure = false;
                // Reopen from the last delivered revision: the undelivered item is replayed.
                await worker.SendInvocationAsync("replay", "server-stream", "changes", 1L);
                await Register(worker, success: true, kind, code, scenario);
                using (var replay = await worker.ReadAsync()) Assert.Equal("worker.event", replay.RootElement.GetProperty("type").GetString());
                using (var complete = await worker.ReadAsync()) Assert.Equal("worker.complete", complete.RootElement.GetProperty("type").GetString());
                Assert.Equal(scenario == "unary" ? 1 : 2, service.Disposals);
                await worker.ShutdownAsync();
            }
            private static async Task Register(WorkerHarness worker, bool success, string kind, string code, string scenario)
            {
                using var registration = await worker.ReadAsync();
                Assert.True(registration.RootElement.GetProperty("type").GetString() == "worker.content-register", worker.Diagnostics.ToString() + registration.RootElement.GetRawText());
                var id = registration.RootElement.GetProperty("id").GetString();
                if (success) await worker.SendAsync(new { type = "host.result", id, value = WorkerHarness.ContentReference("content", 1) });
                else await worker.SendAsync(new { type = "host.error", id, error = new {
                    kind, code, message = "Authenticated registration failure.",
                    dispatchState = scenario == "dispatched" ? "possibly-dispatched" : "not-dispatched",
                    retrySafety = scenario == "unsafe" ? "unsafe" : "safe" } });
            }
        }
        """;

    [Theory]
    [InlineData("Task<SunderContent>", "true")]
    [InlineData("IAsyncEnumerable<string>", "true")]
    [InlineData("IAsyncEnumerable<SunderContent>", "\"true\"")]
    [InlineData("IAsyncEnumerable<SunderContent>", "MissingConstant")]
    public void ResponseRetirement_RejectsUnaryNoncontentAndMalformedOptions(string returns, string option)
    {
        var result = Compile($$"""
            using System.Collections.Generic; using System.Threading.Tasks; using Sunder.Sdk.Services;
            [SunderService] public sealed partial class InvalidService {
                [SunderMethod(CompleteOnResponseContentRetirement = {{option}})]
                public {{returns}} ReadAsync() => throw new System.NotImplementedException();
            }
            """);
        Assert.False(result.Success);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Empty(result.Contracts);
    }

    [Fact]
    public void ResponseRetirement_OwnedInterfaceImplementationKeepsItsPolicyLocal()
    {
        const string source = """
            using System.Collections.Generic; using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)] public interface WatchService { IAsyncEnumerable<SunderContent> WatchAsync(); }
            [SunderServiceProvider(typeof(WatchService))] public sealed partial class Provider : WatchService {
                [SunderMethod(CompleteOnResponseContentRetirement = true)]
                public IAsyncEnumerable<SunderContent> WatchAsync() => throw new System.NotImplementedException();
            }
            """;
        var result = Compile(source);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var contract = Assert.Single(result.Contracts);
        var generated = SunderRpcCSharpImplementationEmitter.Emit(result.Contracts);
        Assert.Contains("SerializeResponseStreamItemAsync", generated, StringComparison.Ordinal);
        AssertCompiles([new("source.cs", source), new("generated.cs", generated),
            new("schema.cs", SunderRpcCSharpProjectedBindings.SchemaSource(contract.GeneratedLogicalName, contract.Descriptor))]);
        Assert.Empty(contract.MethodPolicies.Services);
    }

    [Theory]
    [InlineData("public sealed class Helper { [SunderMethod(CompleteOnResponseContentRetirement = true)] public IAsyncEnumerable<SunderContent> ReadAsync() => throw new System.NotImplementedException(); }")]
    [InlineData("[SunderService] public interface WatchService { [SunderMethod(CompleteOnResponseContentRetirement = true)] IAsyncEnumerable<SunderContent> ReadAsync(); }")]
    public void ResponseRetirement_RejectsUnboundAndAbstractPolicyDeclarations(string declaration)
    {
        var result = Compile("using System.Collections.Generic; using Sunder.Sdk.Services; " + declaration);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.method-invalid");
    }

    [Fact]
    public void ResponseRetirement_AllowsNestedContentWithoutTreatingPropertyNamesAsSchemaKeywords()
    {
        const string source = """
            using System.Collections.Generic; using Sunder.Sdk.Services;
            [SunderService] public sealed partial class WatchService {
                [SunderMethod(CompleteOnResponseContentRetirement = true)]
                public IAsyncEnumerable<Item> ReadAsync() => throw new System.NotImplementedException();
            }
            public sealed record Item(string LogicalType, IReadOnlyList<SunderContent?> Values);
            """;
        var result = Compile(source);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var contract = Assert.Single(result.Contracts);
        AssertCompiles([new("source.cs", source), new("generated.cs", SunderRpcCSharpImplementationEmitter.Emit(result.Contracts)),
            new("schema.cs", SunderRpcCSharpProjectedBindings.SchemaSource(contract.GeneratedLogicalName, contract.Descriptor))]);
    }
}
