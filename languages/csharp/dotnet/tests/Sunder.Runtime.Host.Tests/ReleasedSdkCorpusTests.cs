using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;
using static Sunder.Core.TestSupport.SdkCorpusInput;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed class ReleasedSdkCorpusTests
{
    [ExternalArtifactFact("SUNDER_REQUIRE_SDK_CORPUS", "SUNDER_COMPATIBILITY_CONTEXT")]
    [Trait("Category", "ExternalArtifact")]
    public Task RetainedDotNetWorkerCompletesRpcAndCancellation() => RunRpcProbeAsync("dotnet-rpc-probe-v1");

    [ExternalArtifactFact("SUNDER_REQUIRE_SDK_CORPUS", "SUNDER_COMPATIBILITY_CONTEXT")]
    [Trait("Category", "ExternalArtifact")]
    public Task RetainedNodeWorkerCompletesRpcAndCancellation() => RunRpcProbeAsync("node-rpc-probe-v1");

    private static async Task RunRpcProbeAsync(string profile)
    {
        var input = Load("old-package-new-host");
        Require(Text(input.Layout, "profile") == profile, "Wrong retained Worker probe profile.");
        using var work = TemporaryDirectoryScope.Create("sunder-sdk-rpc-corpus");
        try
        {
            var package = await ValidatePackageAsync(input, work.RootPath);
            await using var host = await SelectedHost.StartAsync(input, work.RootPath, candidate: true);
            await host.AssertBaselineAsync(package, input.PackageId, input.PackageVersion);
            await host.AssertRpcProbeAsync(input);
            await host.ShutdownAsync();
            Assert.Single(host.WorkerStarts());
        }
        finally { input.VerifyUnchanged(); }
    }

    [ExternalArtifactFact("SUNDER_REQUIRE_SDK_CORPUS", "SUNDER_COMPATIBILITY_CONTEXT")]
    [Trait("Category", "ExternalArtifact")]
    public async Task OldPackageRunsOnSelectedCandidateHost()
    {
        var input = Load("old-package-new-host");
        Require(Text(input.Layout, "profile") == "dotnet-worker-v1", "Use the owning RPC/App selector for its explicit profile.");
        using var work = TemporaryDirectoryScope.Create("sunder-sdk-binary-corpus");
        try
        {
            var package = await ValidatePackageAsync(input, work.RootPath);
            await using var host = await SelectedHost.StartAsync(input, work.RootPath, candidate: true);
            await host.AssertBaselineAsync(package, input.PackageId, input.PackageVersion);
            await host.ShutdownAsync();
            Assert.Single(host.WorkerStarts());
        }
        finally { input.VerifyUnchanged(); }
    }

    [ExternalArtifactFact("SUNDER_REQUIRE_SDK_CORPUS", "SUNDER_COMPATIBILITY_CONTEXT")]
    [Trait("Category", "ExternalArtifact")]
    public async Task NewFeatureIsRejectedBySelectedReferenceHost()
    {
        var input = Load("new-feature-old-host");
        using var work = TemporaryDirectoryScope.Create("sunder-sdk-admission-corpus");
        try
        {
            var baseline = await ValidatePackageAsync(input, work.RootPath);
            var rejection = input.Layout.GetProperty("rejection");
            Members(rejection, "mode", "capability", "featureArtifactId");
            var synthetic = Text(rejection, "mode") == "synthetic-unknown-capability";
            Require(synthetic || Text(rejection, "mode") == "retained-feature", "Unknown admission mode.");
            var capability = Text(rejection, "capability");
            Require(System.Text.RegularExpressions.Regex.IsMatch(capability, @"^[a-z][a-z0-9.-]+\.v[1-9][0-9]*$"), "Invalid feature capability.");
            string feature;
            if (synthetic)
            {
                Require(capability == "compatibility.synthetic-future.v1" && Text(rejection, "featureArtifactId") == "",
                    "Synthetic requirements must be explicitly identified.");
                feature = AddSyntheticRequirement(baseline, work.GetPath("synthetic-feature.sunderpkg"), capability);
            }
            else
            {
                feature = input.Artifact(Text(rejection, "featureArtifactId"), "package");
            }
            var inspected = await SunderPackageArchiveInspector.ExtractAndValidateAsync(feature, work.GetPath("feature-inspected"));
            Assert.True(inspected.Success, string.Join("\n", inspected.Errors));
            Assert.Contains(inspected.Manifest!.Targets!, target => target!.Role == "runtime"
                && target.Rid == Text(input.Context, "actualRid") && target.RequiredHostCapabilities!.Contains(capability));

            await using (var old = await SelectedHost.StartAsync(input, work.CreateDirectory("reference"), candidate: false))
            {
                await old.AssertRejectionAsync(feature, capability);
                Assert.Empty((await old.GetAsync("packages/installed")).EnumerateArray());
                Assert.Empty((await old.GetAsync("packages/active")).EnumerateArray());
                Assert.Empty(old.WorkerStarts()); // Trace belongs to the selected binary; not a current-validator mock.
                await old.AssertBaselineAsync(baseline, input.PackageId, input.PackageVersion);
                // The positive control also proves that this Host actually emits worker-start trace records.
                await old.ShutdownAsync();
                // Shutdown flushes the trace; a delayed start record from the rejected
                // attempt cannot hide behind the succeeding one-Worker control.
                Assert.Single(old.WorkerStarts());
            }
            await using var current = await SelectedHost.StartAsync(input, work.CreateDirectory("candidate"), candidate: true);
            if (synthetic)
            {
                await current.AssertRejectionAsync(feature, capability);
                Assert.Empty(current.WorkerStarts());
                await current.AssertBaselineAsync(baseline, input.PackageId, input.PackageVersion);
            }
            else
            {
                using var zip = ZipFile.OpenRead(feature);
                using var doc = JsonDocument.Parse(zip.GetEntry(SunderPackageFormat.ManifestPath)!.Open());
                await current.AssertBaselineAsync(feature, Text(doc.RootElement, "id"), Text(doc.RootElement, "version"));
            }
            await current.ShutdownAsync();
            Assert.Single(current.WorkerStarts());
        }
        finally { input.VerifyUnchanged(); }
    }

    private static async Task<string> ValidatePackageAsync(SdkCorpusInput input, string work)
    {
        var package = input.Artifact(Text(input.Layout, "packageArtifactId"), "package");
        var inspected = await SunderPackageArchiveInspector.ExtractAndValidateAsync(package, Path.Combine(work, "package-inspected"));
        Assert.True(inspected.Success, string.Join("\n", inspected.Errors));
        Assert.Equal(input.PackageId, inspected.Manifest!.Id);
        Assert.Equal(input.PackageVersion, inspected.Manifest.Version);
        Assert.Contains(inspected.Manifest.Targets!, target => target!.Role == "runtime" && target.Kind == "worker"
            && target.Rid == Text(input.Context, "actualRid"));
        return package;
    }

    internal static string AddSyntheticRequirement(string baseline, string destination, string capability)
    {
        using var zip = ZipFile.OpenRead(baseline);
        Require(zip.Entries.Count <= 8192 && zip.Entries.Sum(x => x.Length) <= 256L * 1024 * 1024,
            "Synthetic admission control exceeds the bounded 256 MiB fixture profile.");
        var fixture = new PackageArchiveFixtureBuilder();
        byte[]? changedManifest = null;
        foreach (var entry in zip.Entries.Where(x => !x.FullName.EndsWith('/')))
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            if (entry.FullName == SunderPackageFormat.ManifestPath)
            {
                var manifest = JsonNode.Parse(bytes)!;
                foreach (var target in manifest["targets"]!.AsArray().Where(x => x!["role"]!.GetValue<string>() == "runtime"))
                    target!["requiredHostCapabilities"]!.AsArray().Add(capability);
                bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
                changedManifest = bytes;
            }
            fixture.AddBytes(entry.FullName, bytes);
        }
        Require(changedManifest is not null, "Missing baseline manifest.");
        fixture.Mutate(SunderPackageFormat.ContentIndexPath, bytes =>
        {
            var index = JsonNode.Parse(bytes)!;
            var record = index["files"]!.AsArray().Single(x => x!["path"]!.GetValue<string>() == SunderPackageFormat.ManifestPath)!;
            record["sha256"] = Convert.ToHexStringLower(SHA256.HashData(changedManifest!));
            record["size"] = changedManifest!.Length;
            return JsonSerializer.SerializeToUtf8Bytes(index);
        });
        return fixture.WriteArchive(destination);
    }

    private sealed class SelectedHost : IAsyncDisposable
    {
        private readonly CapturedProcess _process;
        private readonly HttpClient _http;
        private readonly string _trace;
        private readonly string _hostRoot;
        private readonly Dictionary<string, string> _hostHashes;
        private readonly Dictionary<string, string> _packageHashes = [];
        private Guid _runtimeInstanceId;
        private string _expectedRid = null!;
        private bool _stopped;

        private SelectedHost(CapturedProcess process, HttpClient http, string trace, string hostRoot)
        {
            _process = process; _http = http; _trace = trace; _hostRoot = hostRoot;
            _hostHashes = Directory.GetFiles(hostRoot, "*", SearchOption.AllDirectories).ToDictionary(x => x, Hash);
        }

        public static async Task<SelectedHost> StartAsync(SdkCorpusInput input, string work, bool candidate)
        {
            work = WorkPath(work);
            var hostRoot = Path.Combine(work, "host");
            Extract(input.HostArtifact(candidate), hostRoot);
            var entry = Path.Combine(hostRoot, "Sunder.Runtime.Host.dll");
            Require(File.Exists(entry) && File.Exists(Path.Combine(hostRoot, "Sunder.Runtime.Host.runtimeconfig.json")),
                "Selected Host ZIP must contain the complete Runtime Host output at its root.");
            var runtimeRoot = Path.Combine(work, "official-runtime");
            Extract(input.Artifact(Text(input.HostSelection(), "runtimeArtifactId"), "other"), runtimeRoot);
            Require(Directory.GetFiles(runtimeRoot, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order()
                .SequenceEqual(new[] { "artifact.json", "runtime.archive" }), "Runtime input must contain only artifact.json and runtime.archive.");
            var artifact = ReadJson(Path.Combine(runtimeRoot, "artifact.json")).Deserialize<RuntimeExecutionRuntimeVendorArtifact>(ExecutionRuntimeStore.JsonOptions)!;
            var node = Text(input.Layout, "profile") == "node-rpc-probe-v1";
            Require(artifact.Identity.Rid == Text(input.Context, "actualRid")
                && artifact.Identity.RuntimeId == (node ? SunderPackageFormat.NodeExecutionRuntimeId : SunderPackageFormat.DotNetExecutionRuntimeId)
                && artifact.Identity.ModuleAbi == (node ? SunderPackageFormat.NodeWorkerModuleAbi : SunderPackageFormat.DotNetWorkerModuleAbi), "Wrong exact official runtime input.");
            var state = Path.Combine(work, "state");
            RuntimeLocalState.EnsureInitialized(state);
            var store = new ExecutionRuntimeStore(new ExecutionRuntimeStoreLayout(state), new TestExecutionRuntimeGarbageCollectionAuthority());
            await store.InitializeAsync();
            var ensured = await store.EnsureAsync(new ExecutionRuntimeAcquisitionPlan(artifact), Path.Combine(runtimeRoot, "runtime.archive"));
            Assert.True((await store.VerifyAsync(ensured.Receipt.Identity, deep: true)).Healthy);
            // Set up only the official-runtime prerequisite, using the existing vendor-verifying harness.
            // Package admission, activation and shutdown below execute in the selected external Host.
            var connection = Path.Combine(work, "connection.json");
            var trace = Path.Combine(work, "trace.jsonl");
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = hostRoot, UseShellExecute = false };
            foreach (var key in start.Environment.Keys.Where(x => x.StartsWith("SUNDER_", StringComparison.OrdinalIgnoreCase)
                || x.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || x.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            start.Environment["SUNDER_RUNTIME_STATE_ROOT"] = state;
            start.Environment["SUNDER_RUNTIME_BEARER_TOKEN"] = token;
            start.Environment["SUNDER_RUNTIME_CONNECTION_FILE"] = connection;
            start.Environment["SUNDER_RUNTIME_TRACE_PATH"] = trace;
            foreach (var argument in new[] { entry, "--urls", "http://127.0.0.1:0" }) start.ArgumentList.Add(argument);
            var process = CapturedProcess.Start(start, maximumOutputCharacters: 8 * 1024 * 1024);
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var result = new SelectedHost(process, http, trace, hostRoot);
            result._expectedRid = Text(input.Context, "actualRid");
            try
            {
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(45))
                {
                    Assert.False(process.HasExited, process.Diagnostics);
                    var info = RuntimeConnectionInfoStore.Load(connection);
                    if (info is not null)
                    {
                        Assert.True(info.RuntimeUrl.IsLoopback);
                        http.BaseAddress ??= new Uri(info.RuntimeUrl, "/api/v1/");
                        var handshake = await http.GetFromJsonAsync<JsonElement>(new Uri(info.RuntimeUrl, "/api/handshake"));
                        Assert.Equal("dev.sunder.runtime", Text(handshake, "protocolIdentity"));
                        result._runtimeInstanceId = handshake.GetProperty("runtimeInstanceId").GetGuid();
                        if ((await result.GetAsync("system")).GetProperty("isReady").GetBoolean()) return result;
                    }
                    await Task.Delay(100);
                }
                throw new InvalidOperationException("Selected Host did not become ready. " + process.Diagnostics);
            }
            catch { await result.DisposeAsync(); throw; }
        }

        public async Task<JsonElement> GetAsync(string path) => await _http.GetFromJsonAsync<JsonElement>(path);

        private async Task<(System.Net.HttpStatusCode Status, JsonElement Body)> ActivateAsync(string package)
        {
            // Local installed packages require independent Registry policy. This profile
            // uses the existing unpublished dev-owner path to execute unchanged archive
            // payloads, rather than fabricating a Registry policy or modifying the Host.
            var folder = Path.Combine(Path.GetDirectoryName(_hostRoot)!, "dev-package-" + Guid.NewGuid().ToString("N"));
            var inspected = await SunderPackageArchiveInspector.ExtractAndValidateAsync(package, folder);
            Assert.True(inspected.Success, string.Join("\n", inspected.Errors));
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories)) _packageHashes.Add(file, Hash(file));
            var owner = "sdk-corpus-" + Guid.NewGuid().ToString("N");
            var request = new DevPackageOwnerMutationRequest(_runtimeInstanceId,
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), Guid.NewGuid().ToString("N"), 1,
                [new DevPackageOwnerFolder(folder, Watch: false)]);
            using var response = await _http.PutAsJsonAsync("dev-package-owners/" + owner, request);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (response.StatusCode, body.RootElement.Clone());
        }

        public async Task AssertRejectionAsync(string package, string capability)
        {
            var (status, body) = await ActivateAsync(package);
            Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, status);
            Assert.Equal("runtime.v1.package-validation", Text(body, "code"));
            Assert.Contains($"requires Host capability '{capability}'", Text(body, "detail"), StringComparison.Ordinal);
            Assert.Contains("does not support it", Text(body, "detail"), StringComparison.Ordinal);
        }

        public async Task AssertBaselineAsync(string package, string id, string version)
        {
            var (status, body) = await ActivateAsync(package);
            Assert.True(status == System.Net.HttpStatusCode.OK, body.ToString() + "\n" + _process.Diagnostics);
            var active = Assert.Single((await GetAsync("packages/active")).EnumerateArray());
            Assert.Equal(id, Text(active, "packageId"));
            Assert.Equal(version, Text(active, "version"));
            Assert.Equal((int)PackageReadinessState.Ready, active.GetProperty("readiness").GetInt32());
            var providers = (await GetAsync("rpc/catalog")).GetProperty("providers").EnumerateArray().ToArray();
            Assert.NotEmpty(providers);
            Assert.All(providers, provider => Assert.Equal(id, Text(provider, "packageId")));
            Assert.NotEmpty(WorkerStarts());
        }

        public async Task AssertRpcProbeAsync(SdkCorpusInput input)
        {
            var expectation = input.Layout.GetProperty("probe");
            Members(expectation, "protocol", "contractId", "contractVersion", "descriptorSha256", "abiSha256", "providerId");
            Assert.Equal("sunder.compatibility.probe.v1", Text(expectation, "protocol"));
            Assert.Equal("compatibility.probe", Text(expectation, "contractId"));
            var provider = Assert.Single((await GetAsync("rpc/catalog")).GetProperty("providers").EnumerateArray());
            foreach (var field in new[] { "contractId", "contractVersion", "descriptorSha256", "abiSha256", "providerId" })
                Assert.Equal(Text(expectation, field), Text(provider, field));
            var nonce = Guid.NewGuid().ToString("N");
            using var challenge = await _http.PutAsJsonAsync($"packages/{input.PackageId}/data/state/compatibility.challenge", new { value = nonce });
            challenge.EnsureSuccessStatusCode();
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                using var failure = await _http.GetAsync($"packages/{input.PackageId}/data/state/compatibility.failure");
                failure.EnsureSuccessStatusCode();
                var failureState = await failure.Content.ReadFromJsonAsync<PackageDataValueResponse>();
                Assert.False(failureState!.Found, failureState.Value + "\n" + _process.Diagnostics);
                using var response = await _http.GetAsync($"packages/{input.PackageId}/data/state/compatibility.report");
                response.EnsureSuccessStatusCode();
                var state = await response.Content.ReadFromJsonAsync<PackageDataValueResponse>();
                if (!state!.Found) { await Task.Delay(50); continue; }
                using var report = JsonDocument.Parse(state.Value!);
                SdkRpcProbeReport.Validate(report.RootElement, nonce);
                // Provider acknowledgement is separately observed, not just a caller's boolean.
                Assert.Equal(nonce, Text(await GetAsync($"packages/{input.PackageId}/data/state/compatibility.provider-cancelled"), "value"));
                return;
            }
            throw new InvalidOperationException("Retained RPC probe did not finish. " + _process.Diagnostics);
        }

        public int[] WorkerStarts()
        {
            if (!File.Exists(_trace)) return [];
            return File.ReadAllLines(_trace).Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => JsonDocument.Parse(x).RootElement.Clone())
                .Where(x => Text(x, "event") == "package.worker.process.started")
                .Select(x =>
                {
                    Assert.Equal(_expectedRid, Text(x, "runtimeIdentifier"));
                    return x.GetProperty("processId").GetInt32();
                }).ToArray();
        }

        public async Task ShutdownAsync()
        {
            using var response = await _http.PostAsync("system/shutdown", null);
            response.EnsureSuccessStatusCode();
            await _process.EnsureSuccessAsync(TimeSpan.FromSeconds(45));
            await CurrentRidRuntimeHostTestSupport.AssertProcessesStoppedAsync(WorkerStarts());
            _stopped = true;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_stopped && !_process.HasExited && _http.BaseAddress is not null) await ShutdownAsync();
            }
            finally
            {
                _http.Dispose();
                await _process.DisposeAsync();
                Assert.Equal(_hostHashes.Keys.Order(), Directory.GetFiles(_hostRoot, "*", SearchOption.AllDirectories).Order());
                foreach (var (path, hash) in _hostHashes) Assert.Equal(hash, Hash(path));
                foreach (var (path, hash) in _packageHashes) Assert.Equal(hash, Hash(path));
            }
        }
    }
}

internal static class SdkRpcProbeReport
{
    internal static void Validate(JsonElement report, string nonce)
    {
        Members(report, "protocol", "nonce", "unary", "stream", "started", "clientCancelled", "providerCancelled", "afterCancel");
        Require(Text(report, "protocol") == "sunder.compatibility.probe.v1" && Text(report, "nonce") == nonce
            && Text(report, "unary") == "echo:" + nonce && Text(report, "afterCancel") == "echo:after:" + nonce
            && report.GetProperty("stream").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { nonce + ":1", nonce + ":2" })
            && report.GetProperty("started").GetBoolean() && report.GetProperty("clientCancelled").GetBoolean()
            && Text(report, "providerCancelled") == nonce, "RPC report is stale, incomplete or did not prove cancellation/drain.");
    }
}
