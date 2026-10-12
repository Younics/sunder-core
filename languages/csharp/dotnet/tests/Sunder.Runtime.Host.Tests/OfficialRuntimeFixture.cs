using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class OfficialRuntimeFixture : IAsyncLifetime
{
    // Historical real releases keep the forward-update case independent of eager cache reuse.
    // Production selection still resolves the latest eligible release from fresh metadata.
    private const string BaselineDotNetVersion = "10.0.11";
    private const string OlderDotNetVersion = "10.0.10";
    private const string CurrentRidNodeVersion = "24.21.0";
    private const string ArchiveCacheVariableName = "SUNDER_OFFICIAL_RUNTIME_ARCHIVE_CACHE";
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private readonly Dictionary<RuntimeBuildKey, ExecutionRuntimeReceiptV1> _receipts = [];
    private readonly TestExecutionRuntimeGarbageCollectionAuthority _garbageCollectionAuthority = new();
    private readonly ExecutionRuntimePhaseTelemetry _telemetry;
    private int _packageSequence;

    public OfficialRuntimeFixture()
        : this(null)
    {
    }

    internal OfficialRuntimeFixture(ExecutionRuntimePhaseTelemetry? telemetry)
    {
        _telemetry = telemetry ?? ExecutionRuntimePhaseTelemetry.Disabled;
        RootPath = Path.Combine(AppContext.BaseDirectory, ".sunder-official-runtime-tests", Guid.NewGuid().ToString("N"));
        Layout = new ExecutionRuntimeStoreLayout(Path.Combine(RootPath, "runtime-store"));
        Paths = new RuntimePackagePaths(Path.Combine(RootPath, "runtime-data"));
        Store = new ExecutionRuntimeStore(
            Layout,
            _garbageCollectionAuthority,
            telemetry: _telemetry,
            materializer: new OfficialRuntimeMaterializer(new OfficialRuntimePlatformProbe()));
        LaunchResolver = new ExecutionRuntimeLaunchResolver(Store, TimeProvider.System, _telemetry);
        LocalBindingResolver = new LocalExecutionRuntimeBindingResolver(Store, TimeProvider.System);
        Catalog = new RuntimeRpcCatalog();
        Owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            rpcCatalog: Catalog);
        Permissions = new RuntimeRpcPermissionStore(Paths);
        Transfers = new RuntimeContentTransferStore(Paths);
        Broker = new RuntimeRpcBroker(
            Catalog,
            Permissions,
            Owner.State,
            Owner,
            hostStopping: CancellationToken.None,
            policy: null,
            timeProvider: null,
            contentStore: Transfers,
            transportPolicy: new RuntimeTransportPolicyOptions());
    }

    internal string RootPath { get; }
    internal ExecutionRuntimeStoreLayout Layout { get; }
    internal RuntimePackagePaths Paths { get; }
    internal ExecutionRuntimeStore Store { get; }
    internal ExecutionRuntimeLaunchResolver LaunchResolver { get; }
    internal ExecutionRuntimeLaunchResolver Resolver => LaunchResolver;
    internal LocalExecutionRuntimeBindingResolver LocalBindingResolver { get; }
    internal RuntimeRpcCatalog Catalog { get; }
    internal RuntimeSessionOwner Owner { get; }
    internal RuntimeRpcPermissionStore Permissions { get; }
    internal RuntimeContentTransferStore Transfers { get; }
    internal RuntimeRpcBroker Broker { get; }
    internal ExecutionRuntimeReceiptV1 DotNetReceipt { get; private set; } = null!;
    internal ExecutionRuntimeReceiptV1 AlternateDotNetReceipt { get; private set; } = null!;
    internal ExecutionRuntimeReceiptV1 NodeReceipt { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(RootPath);
        await Store.InitializeAsync();
        DotNetReceipt = await EnsureDotNetAsync(BaselineDotNetVersion);
        AlternateDotNetReceipt = await EnsureDotNetAsync(OlderDotNetVersion);
        NodeReceipt = await EnsureNodeAsync(CurrentRidNodeVersion);
    }

    public Task DisposeAsync()
    {
        _ensureGate.Dispose();
        Catalog.Dispose();
        Permissions.Dispose();
        Transfers.Dispose();
        _ = ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(RootPath);
        return Task.CompletedTask;
    }

    internal Task<ExecutionRuntimeReceiptV1> EnsureDotNetAsync(string version)
        => EnsureDotNetAsync(version, rid: null);

    internal Task<ExecutionRuntimeReceiptV1> EnsureNodeAsync(string version)
        => EnsureNodeCoreAsync(version);

    internal ExecutionRuntimeReceiptV1 GetReceipt(SyntheticRuntimeKind kind)
        => kind == SyntheticRuntimeKind.DotNet ? DotNetReceipt : NodeReceipt;

    internal string GetRuntimeRoot(ExecutionRuntimeReceiptV1 receipt)
        => Layout.GetContentRootPath(receipt.ContentIdentity);

    internal string GetLauncherPath(ExecutionRuntimeReceiptV1 receipt)
        => Path.Combine([GetRuntimeRoot(receipt), .. receipt.LauncherPath.Split('/')]);

    internal static InstalledPackageExecutionRuntimeBindingRecord CreateBinding(ExecutionRuntimeReceiptV1 receipt)
    {
        var requirement = RequirementFor(receipt.Identity.RuntimeId, receipt.Identity.ModuleAbi);
        return new(
            SunderPackageFormat.RuntimeHostRole,
            receipt.Identity.Rid,
            requirement,
            receipt.Identity.ToContract(),
            receipt.InstalledArtifact,
            DateTimeOffset.UtcNow);
    }

    internal PreparedRuntimePackage CreatePackage(
        SyntheticRuntimeKind kind,
        string packageId,
        string mode,
        ExecutionRuntimeReceiptV1? exactReceipt = null)
    {
        var receipt = exactReceipt ?? GetReceipt(kind);
        var expectedRuntimeId = kind == SyntheticRuntimeKind.DotNet
            ? SunderPackageFormat.DotNetExecutionRuntimeId
            : SunderPackageFormat.NodeExecutionRuntimeId;
        if (!string.Equals(receipt.Identity.RuntimeId, expectedRuntimeId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The exact runtime receipt does not match the package runtime kind.", nameof(exactReceipt));
        }

        var sequence = Interlocked.Increment(ref _packageSequence).ToString("D4");
        var packageRoot = Path.Combine(RootPath, "packages", sequence + "-" + packageId);
        var sourceRoot = Path.Combine(packageRoot, "source");
        var projectionRoot = Path.Combine(packageRoot, "projection");
        var physicalRoot = Path.Combine(sourceRoot, "payload", SunderPackageFormat.RuntimeHostRole, receipt.Identity.Rid);
        Directory.CreateDirectory(physicalRoot);
        Directory.CreateDirectory(projectionRoot);

        if (kind == SyntheticRuntimeKind.Node)
        {
            WriteNodeWorkerPayload(physicalRoot);
            WriteNodeWorkerPayload(projectionRoot);
        }
        else
        {
            TestWorkerFixture.CopyPayload(physicalRoot, receipt.Identity.Rid);
            TestWorkerFixture.CopyPayload(projectionRoot, receipt.Identity.Rid);
        }
        var entryPointName = kind == SyntheticRuntimeKind.DotNet ? TestWorkerFixture.AssemblyFileName : "worker.cjs";

        var physicalMode = Path.Combine(physicalRoot, "mode.txt");
        var projectedMode = Path.Combine(projectionRoot, "mode.txt");
        File.WriteAllText(physicalMode, mode);
        File.WriteAllText(projectedMode, mode);

        var targetKey = new SunderPackageTargetKey(SunderPackageFormat.RuntimeHostRole, receipt.Identity.Rid);
        var target = new SunderPackageTargetManifest
        {
            Role = targetKey.Role,
            Rid = targetKey.Rid,
            Kind = SunderPackageFormat.WorkerTargetKind,
            EntryPoint = entryPointName,
            TargetFramework = kind == SyntheticRuntimeKind.DotNet ? "net10.0" : "node24",
            SdkVersion = "1.1.0",
            RequiredHostCapabilities =
            [
                "core.v1",
                "execution-runtime.v1",
                "rpc.v1",
                "sdk-baseline-1-1.v1",
                "worker-protocol.v1",
            ],
            ExecutionRuntime = new SunderPackageExecutionRuntimeManifest
            {
                Id = receipt.Identity.RuntimeId,
                VersionRange = kind == SyntheticRuntimeKind.DotNet ? ">=10.0.0 <11.0.0" : ">=24.18.1 <25.0.0",
                ModuleAbi = receipt.Identity.ModuleAbi,
            },
        };
        var manifest = new SunderPackageManifest
        {
            ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
            ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
            Id = packageId,
            Name = packageId,
            Version = "1.0.0",
            Targets = [target],
            DependsOn = [],
            ContractBundles = [],
            UsesContracts = [],
            Provides = [],
        };
        var entries = Directory.EnumerateFiles(physicalRoot, "*", SearchOption.AllDirectories)
            .Select(path => CreatePackageContentEntry(path, "payload/" + targetKey.Role + "/" + targetKey.Rid + "/"
                + Path.GetRelativePath(physicalRoot, path).Replace(Path.DirectorySeparatorChar, '/')))
            .OrderBy(static item => item.Path, StringComparer.Ordinal)
            .ToArray();
        var content = new SunderPackageContentIndex(SunderPackageFormat.CurrentContentIndexVersion, entries);
        var contentIdentity = Hash(JsonSerializer.SerializeToUtf8Bytes(content));
        var manifestSha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(manifest));
        var source = new RuntimePackageSource(
            packageId,
            PackageSourceKind.Installed,
            sourceRoot,
            sourceRoot,
            PackageHostRoles.Runtime,
            [],
            contentIdentity,
            manifest,
            content,
            targetKey,
            target);
        return new PreparedRuntimePackage(
            sourceRoot,
            source,
            projectionRoot,
            packageId,
            manifest.Version!,
            PackageHostRoles.Runtime,
            new RuntimePackageActivationState(packageId, manifest.Name!, manifest.Version!, PackageHostRoles.Runtime, null, targetKey, target),
            Path.Combine(projectionRoot, entryPointName),
            targetKey,
            target,
            [],
            new Dictionary<string, Sunder.Sdk.Rpc.SunderRpcContractDescriptor>(StringComparer.Ordinal),
            manifestSha256)
        {
            ExecutionRuntimeBinding = new RuntimePackageExecutionRuntimeBinding(
                targetKey.Role,
                targetKey.Rid,
                RequirementFor(receipt.Identity.RuntimeId, receipt.Identity.ModuleAbi),
                receipt.Identity.ToContract(),
                receipt.InstalledArtifact,
                DateTimeOffset.UtcNow),
        };
    }

    internal async Task<ActivatedSyntheticWorker> CreateWorkerAsync(
        PreparedRuntimePackage package,
        RuntimeProcessPolicyOptions? policy = null,
        ICollection<string>? activationWarnings = null)
    {
        var warnings = activationWarnings ?? new List<string>();
        var errors = new List<string>();
        var result = await new RuntimePackageActivator(
                NullLogger.Instance,
                Paths,
                Broker,
                policy,
                CancellationToken.None,
                Owner.State,
                LaunchResolver,
                _telemetry)
            .ActivateAsync(package, warnings, errors, CancellationToken.None);
        if (!result.Success || result.LoadedPackage is not ActiveLoadedPackage loaded)
        {
                throw new InvalidOperationException("Official process package activation failed: " + string.Join(" | ", errors));
        }
        if (loaded.Worker is not ProcessRuntimeWorker worker || loaded.RuntimeActivationId == Guid.Empty)
        {
                throw new InvalidOperationException("Official process package did not create a process worker.");
        }
        return new(worker, loaded.RuntimeActivationId, warnings.ToArray());
    }

    internal async Task<IReadOnlyList<ExecutionRuntimeProcessLeaseV1>> ReadLeasesAsync()
    {
        if (!Directory.Exists(Layout.LeaseRootPath)) return [];
        var leases = new List<ExecutionRuntimeProcessLeaseV1>();
        foreach (var path in Directory.EnumerateFiles(Layout.LeaseRootPath, "*.json").OrderBy(static path => path, StringComparer.Ordinal))
        {
            leases.Add(JsonSerializer.Deserialize<ExecutionRuntimeProcessLeaseV1>(
                           await File.ReadAllBytesAsync(path),
                           ExecutionRuntimeStore.JsonOptions)
                       ?? throw new InvalidDataException($"Execution-runtime lease '{path}' was empty."));
        }
        return leases;
    }

    private async Task<ExecutionRuntimeReceiptV1> EnsureDotNetAsync(string version, string? rid)
    {
        var targetRid = rid ?? PackageTargetSelection.GetCurrentRuntimeIdentifier();
        return await EnsureAsync(new RuntimeBuildKey(SyntheticRuntimeKind.DotNet, version, targetRid));
    }

    private async Task<ExecutionRuntimeReceiptV1> EnsureNodeCoreAsync(string version)
    {
        return await EnsureAsync(new RuntimeBuildKey(
            SyntheticRuntimeKind.Node,
            version,
            PackageTargetSelection.GetCurrentRuntimeIdentifier()));
    }

    private async Task<ExecutionRuntimeReceiptV1> EnsureAsync(RuntimeBuildKey key)
    {
        await _ensureGate.WaitAsync();
        try
        {
            if (_receipts.TryGetValue(key, out var receipt)) return receipt;
            var artifact = await ResolveOfficialArtifactAsync(key);
            var archivePath = await GetVerifiedOfficialArchiveAsync(artifact);
            var installed = await Store.EnsureAsync(new ExecutionRuntimeAcquisitionPlan(artifact), archivePath);
            var verification = await Store.VerifyAsync(installed.Receipt.Identity, deep: true);
            if (!verification.Healthy)
            {
                throw new InvalidDataException("Official runtime failed deep verification: " + verification.Message);
            }
            _receipts.Add(key, installed.Receipt);
            return installed.Receipt;
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    private static async Task<RuntimeExecutionRuntimeVendorArtifact> ResolveOfficialArtifactAsync(RuntimeBuildKey key)
    {
        using var http = new OfficialRuntimeHttpClient(new DefaultHttpClientFactory());
        var resolver = new OfficialRuntimeResolver(http, TimeProvider.System);
        var runtimeId = key.Kind == SyntheticRuntimeKind.DotNet
            ? SunderPackageFormat.DotNetExecutionRuntimeId
            : SunderPackageFormat.NodeExecutionRuntimeId;
        var moduleAbi = key.Kind == SyntheticRuntimeKind.DotNet
            ? SunderPackageFormat.DotNetWorkerModuleAbi
            : SunderPackageFormat.NodeWorkerModuleAbi;
        var range = key.Kind == SyntheticRuntimeKind.DotNet ? ">=10.0.0 <11.0.0" : ">=24.18.1 <25.0.0";
        return await resolver.ResolveAsync(new(runtimeId, range, moduleAbi), key.Rid, CancellationToken.None, key.Version);
    }

    private async Task<string> GetVerifiedOfficialArchiveAsync(RuntimeExecutionRuntimeVendorArtifact artifact)
    {
        var cacheRoot = Environment.GetEnvironmentVariable(ArchiveCacheVariableName);
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            cacheRoot = Path.Combine(Path.GetTempPath(), "sunder-official-runtime-archives");
        }
        Directory.CreateDirectory(cacheRoot);
        var prefix = artifact.Identity.SourceId == ConfiguredOfficialSourceId.DotNet ? "DotNet" : "Node";
        var cachePath = Path.Combine(cacheRoot, prefix + "-" + artifact.Identity.VendorChecksumDigest + ".archive");
        if (File.Exists(cachePath))
        {
            VerifyVendorChecksum(artifact, cachePath);
            return cachePath;
        }

        var downloadRoot = Path.Combine(RootPath, "official-downloads");
        Directory.CreateDirectory(downloadRoot);
        var downloadPath = Path.Combine(downloadRoot, Path.GetFileName(cachePath));
        using var http = new OfficialRuntimeHttpClient(new DefaultHttpClientFactory());
        using var response = await http.OpenAsync(
            new Uri(artifact.Distribution.DownloadUrl),
            artifact.Identity.SourceId,
            CancellationToken.None,
            artifact.Identity.VendorVersion,
            artifact.Identity.Rid);
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var destination = new FileStream(downloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await CopyBoundedAsync(source, destination, OfficialRuntimeSourcePolicy.MaxArchiveBytes, CancellationToken.None);
        }
        VerifyVendorChecksum(artifact, downloadPath);
        File.Copy(downloadPath, cachePath, overwrite: true);
        return cachePath;
    }

    private static void VerifyVendorChecksum(RuntimeExecutionRuntimeVendorArtifact artifact, string path)
    {
        var digest = artifact.Identity.VendorChecksumAlgorithm == RuntimeExecutionRuntimeChecksumAlgorithm.Sha512
            ? HashFile(path, SHA512.Create())
            : HashFile(path, SHA256.Create());
        if (!string.Equals(digest, artifact.Identity.VendorChecksumDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Cached official runtime archive failed vendor checksum verification.");
        }
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long limit, CancellationToken token)
    {
        var buffer = new byte[128 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, token);
            if (read == 0) return;
            copied += read;
            if (copied > limit) throw new InvalidDataException("Official runtime archive exceeds its byte limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private static void WriteNodeWorkerPayload(string root)
    {
        File.WriteAllText(Path.Combine(root, "worker.cjs"), NodeWorkerSource);
    }

    private static RuntimeExecutionRuntimeRequirement RequirementFor(string runtimeId, string moduleAbi)
        => new(runtimeId, runtimeId == SunderPackageFormat.DotNetExecutionRuntimeId ? ">=10.0.0 <11.0.0" : ">=24.18.1 <25.0.0", moduleAbi);

    private static SunderPackageContentIndexEntry CreatePackageContentEntry(string path, string archivePath)
    {
        using var stream = File.OpenRead(path);
        return new(archivePath, Hash(stream), stream.Length);
    }

    private static string Hash(Stream stream)
        => Convert.ToHexStringLower(SHA256.HashData(stream));

    private static string Hash(byte[] bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string HashFile(string path, HashAlgorithm algorithm)
    {
        using (algorithm)
        using (var stream = File.OpenRead(path))
        {
            return Convert.ToHexStringLower(algorithm.ComputeHash(stream));
        }
    }

    private sealed class DefaultHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(OfficialRuntimeHttpClient.CreateHandler(), disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
    }

    private sealed record RuntimeBuildKey(
        SyntheticRuntimeKind Kind,
        string Version,
        string Rid)
    {
        public string IdentityText => string.Join('-', Kind, Version, Rid).Replace(Path.DirectorySeparatorChar, '-');
    }

    private const string NodeWorkerSource = """
        const fs = require('fs');
        const path = require('path');
        const input = process.stdin;
        const output = process.stdout;
        let buffer = Buffer.alloc(0);
        input.on('data', chunk => { buffer = Buffer.concat([buffer, chunk]); pump(); });
        input.on('end', () => process.exit(0));

        function pump() {
          while (true) {
            const headerEnd = buffer.indexOf('\r\n\r\n');
            if (headerEnd < 0) return;
            const header = buffer.slice(0, headerEnd).toString('utf8');
            const match = /Content-Length: (\d+)/i.exec(header);
            if (!match) process.exit(64);
            const length = Number(match[1]);
            const start = headerEnd + 4;
            if (buffer.length < start + length) return;
            const body = buffer.slice(start, start + length);
            buffer = buffer.slice(start + length);
            handle(JSON.parse(body.toString('utf8'))).catch(error => {
              console.error(error && error.stack ? error.stack : String(error));
              process.exit(70);
            });
          }
        }

        async function handle(message) {
          if (message.type === 'host.hello') {
            write({
              type: 'worker.ready',
              protocol: message.protocol,
              protocolVersion: message.protocolVersion,
              challenge: message.challenge,
              packageId: message.packageId,
              packageVersion: message.packageVersion,
              activationId: message.activationId,
              sessionId: message.sessionId,
              resourceProfileHash: message.resourceProfileHash,
              contributions: {
                settingsSchema: null,
                storageMigrations: [],
                callbackHandlers: [],
                authHandler: false,
                rpcProviders: message.providers,
                candidateLifecycle: false,
                generationLifecycle: false,
                migrationPreparationLifecycle: true
              }
            });
          } else if (message.type === 'host.start-candidate') {
            write({ type: 'worker.candidate-started' });
          } else if (message.type === 'host.commit-generation') {
            write({ type: 'worker.generation-committed', activationId: message.activationId, sessionGeneration: message.sessionGeneration });
          } else if (message.type === 'host.prepare-storage-migrations') {
            write({ type: 'worker.storage-migrations-prepared', activationId: message.activationId, sessionGeneration: message.sessionGeneration });
          } else if (message.type === 'host.activate') {
            const temp = process.env.SUNDER_WORKER_TEMP_ROOT;
            const status = {
              contentRoot: process.env.SUNDER_PACKAGE_CONTENT_ROOT,
              temporaryRoot: temp,
              workspaceRoot: message.workspaceRootPath,
              workspaceEnvironment: process.env.SUNDER_PACKAGE_WORKSPACE_ROOT ?? null,
              legacyDataPath: process.env.SUNDER_PACKAGE_DATA_PATH ?? null,
              environmentNames: Object.keys(process.env)
            };
            fs.writeFileSync(path.join(temp, 'v1-environment.json'), JSON.stringify(status));
            write({ type: 'worker.activated', sessionGeneration: message.sessionGeneration });
          } else if (message.type === 'host.shutdown') {
            write({ type: 'worker.shutdown-ack', shutdownId: message.shutdownId });
            process.exit(0);
          }
        }

        function write(value) {
          const body = Buffer.from(JSON.stringify(value), 'utf8');
          output.write(`Content-Length: ${body.length}\r\n\r\n`);
          output.write(body);
        }
        """;
}

internal enum SyntheticRuntimeKind
{
    DotNet,
    Node,
}

internal sealed record ActivatedSyntheticWorker(
    ProcessRuntimeWorker Worker,
    Guid ActivationId,
    IReadOnlyList<string> Warnings);
