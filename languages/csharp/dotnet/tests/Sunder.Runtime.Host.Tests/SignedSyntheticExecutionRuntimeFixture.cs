using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class SignedSyntheticExecutionRuntimeFixture : IAsyncLifetime
{
    private const string LauncherName = "Sunder.Runtime.Host.Tests.SyntheticExecutionRuntimeLauncher";
    private const string ReleaseKeyId = "sunder.test.synthetic-runtime.v1";
    private const string AttestationKeyId = "sunder.test.synthetic-attestation.v1";
    private const string RegistryOrigin = "https://registry.example/";
    private static readonly byte[] ReleaseKey = SHA256.HashData("sunder-signed-synthetic-runtime-gate"u8);
    private readonly TestExecutionRuntimeGarbageCollectionAuthority _garbageCollectionAuthority = new();
    private readonly ExecutionRuntimePhaseTelemetry _telemetry;
    private int _packageSequence;

    public SignedSyntheticExecutionRuntimeFixture()
        : this(telemetry: null)
    {
    }

    internal SignedSyntheticExecutionRuntimeFixture(ExecutionRuntimePhaseTelemetry? telemetry)
    {
        _telemetry = telemetry ?? ExecutionRuntimePhaseTelemetry.Disabled;
        RootPath = Path.Combine(
            AppContext.BaseDirectory,
            ".sunder-signed-synthetic-runtime-tests",
            Guid.NewGuid().ToString("N"));
        Layout = new ExecutionRuntimeStoreLayout(Path.Combine(RootPath, "runtime-store"));
        Paths = new RuntimePackagePaths(Path.Combine(RootPath, "runtime-data"));
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
        var trust = new ExecutionRuntimeHostTrustService(
            "synthetic-process-gate",
            generation: 1,
            developmentTrustEnabled: false,
            productionKeys: [new ExecutionRuntimeHostTrustKey(ReleaseKeyId, ReleaseKey, 1)]);
        Store = new ExecutionRuntimeStore(
            Layout,
            trust,
            _garbageCollectionAuthority,
            signatureVerifier: new TestSignatureVerifier(),
            telemetry: _telemetry);
        Resolver = new ExecutionRuntimeLaunchResolver(
            Store,
            new ExecutionRuntimeLifecyclePolicyCache(Layout),
            TimeProvider.System,
            _telemetry);
    }

    internal string RootPath { get; }
    internal ExecutionRuntimeStoreLayout Layout { get; }
    internal RuntimePackagePaths Paths { get; }
    internal RuntimeRpcCatalog Catalog { get; }
    internal RuntimeSessionOwner Owner { get; }
    internal RuntimeRpcPermissionStore Permissions { get; }
    internal RuntimeContentTransferStore Transfers { get; }
    internal RuntimeRpcBroker Broker { get; }
    internal ExecutionRuntimeStore Store { get; }
    internal ExecutionRuntimeLaunchResolver Resolver { get; }
    internal ExecutionRuntimeReceiptV1 DotNetReceipt { get; private set; } = null!;
    internal ExecutionRuntimeReceiptV1 AlternateDotNetReceipt { get; private set; } = null!;
    internal ExecutionRuntimeReceiptV1 AlternateRidDotNetReceipt { get; private set; } = null!;
    internal ExecutionRuntimeReceiptV1 NodeReceipt { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(RootPath);
        await Store.InitializeAsync();
        var payload = await CreateLauncherPayloadAsync();
        DotNetReceipt = await InstallAsync(
            SyntheticRuntimeKind.DotNet,
            runtimeVersion: "10.0.1",
            packagingRevision: 1,
            payload);
        AlternateDotNetReceipt = await InstallAsync(
            SyntheticRuntimeKind.DotNet,
            runtimeVersion: "10.0.2",
            packagingRevision: 2,
            payload);
        AlternateRidDotNetReceipt = await InstallAsync(
            SyntheticRuntimeKind.DotNet,
            runtimeVersion: "10.0.1",
            packagingRevision: 1,
            payload,
            runtimeIdentifier: GetAlternateRuntimeIdentifier());
        NodeReceipt = await InstallAsync(
            SyntheticRuntimeKind.Node,
            runtimeVersion: "24.18.1",
            packagingRevision: 1,
            payload);
    }

    public Task DisposeAsync()
    {
        Catalog.Dispose();
        Permissions.Dispose();
        Transfers.Dispose();
        _ = ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(RootPath);
        return Task.CompletedTask;
    }

    internal ExecutionRuntimeReceiptV1 GetReceipt(SyntheticRuntimeKind kind)
        => kind == SyntheticRuntimeKind.DotNet ? DotNetReceipt : NodeReceipt;

    internal string GetRuntimeRoot(ExecutionRuntimeReceiptV1 receipt)
        => Layout.GetContentRootPath(receipt.ContentIdentity);

    internal string GetLauncherPath(ExecutionRuntimeReceiptV1 receipt)
        => Path.Combine([GetRuntimeRoot(receipt), .. receipt.LauncherPath.Split('/')]);

    internal PreparedRuntimePackage CreatePackage(
        SyntheticRuntimeKind kind,
        string packageId,
        string mode,
        ExecutionRuntimeReceiptV1? exactReceipt = null)
    {
        var receipt = exactReceipt ?? GetReceipt(kind);
        var expectedRuntimeId = kind == SyntheticRuntimeKind.DotNet
            ? SunderExecutionRuntimeFormat.DotNetRuntimeId
            : SunderExecutionRuntimeFormat.NodeRuntimeId;
        if (!string.Equals(receipt.Identity.RuntimeId, expectedRuntimeId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The exact runtime receipt does not match the package runtime kind.", nameof(exactReceipt));
        }
        var sequence = Interlocked.Increment(ref _packageSequence).ToString("D4");
        var packageRoot = Path.Combine(RootPath, "packages", sequence + "-" + packageId);
        var sourceRoot = Path.Combine(packageRoot, "source");
        var projectionRoot = Path.Combine(packageRoot, "projection");
        var physicalRoot = Path.Combine(
            sourceRoot,
            "payload",
            SunderPackageFormat.RuntimeHostRole,
            receipt.Identity.Rid);
        Directory.CreateDirectory(physicalRoot);
        Directory.CreateDirectory(projectionRoot);

        var entryPointName = kind == SyntheticRuntimeKind.DotNet ? "worker.dll" : "worker.cjs";
        var workerSource = TestWorkerFixture.AssemblyPath;
        var physicalEntryPoint = Path.Combine(physicalRoot, entryPointName);
        var projectedEntryPoint = Path.Combine(projectionRoot, entryPointName);
        File.Copy(workerSource, physicalEntryPoint);
        File.Copy(workerSource, projectedEntryPoint);
        var physicalMode = Path.Combine(physicalRoot, "mode.txt");
        var projectedMode = Path.Combine(projectionRoot, "mode.txt");
        File.WriteAllText(physicalMode, mode);
        File.WriteAllText(projectedMode, mode);

        var targetKey = new SunderPackageTargetKey(
            SunderPackageFormat.RuntimeHostRole,
            receipt.Identity.Rid);
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
                VersionRange = kind == SyntheticRuntimeKind.DotNet
                    ? ">=10.0.0 <11.0.0"
                    : ">=24.18.1 <25.0.0",
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
        var content = new SunderPackageContentIndex(
            SunderPackageFormat.CurrentContentIndexVersion,
            new[]
            {
                CreatePackageContentEntry(physicalEntryPoint, $"payload/{targetKey.Role}/{targetKey.Rid}/{entryPointName}"),
                CreatePackageContentEntry(physicalMode, $"payload/{targetKey.Role}/{targetKey.Rid}/mode.txt"),
            }.OrderBy(static item => item.Path, StringComparer.Ordinal).ToArray());
        var contentBytes = JsonSerializer.SerializeToUtf8Bytes(content);
        var contentIdentity = Hash(contentBytes);
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
        var policyObservedAtUtc = DateTimeOffset.Parse("2026-08-30T10:00:00Z");
        return new PreparedRuntimePackage(
            sourceRoot,
            source,
            projectionRoot,
            packageId,
            manifest.Version!,
            PackageHostRoles.Runtime,
            new RuntimePackageActivationState(
                packageId,
                manifest.Name!,
                manifest.Version!,
                PackageHostRoles.Runtime,
                null,
                targetKey,
                target),
            projectedEntryPoint,
            targetKey,
            target,
            [],
            new Dictionary<string, Sunder.Sdk.Rpc.SunderRpcContractDescriptor>(StringComparer.Ordinal),
            manifestSha256)
        {
            ExecutionRuntimeBinding = new RuntimePackageExecutionRuntimeBinding(
                targetKey.Role,
                targetKey.Rid,
                receipt.Identity.RuntimeId,
                receipt.Identity.Version,
                receipt.Identity.PackagingRevision,
                receipt.Identity.ModuleAbi,
                receipt.ManifestSha256,
                receipt.ArtifactSha256,
                receipt.ArtifactSize,
                receipt.RegistryOrigin!,
                receipt.AcquisitionSource,
                receipt.SigningKeyId,
                receipt.TrustGeneration,
                RuntimePolicyRevision: 1,
                RuntimeLifecycleState: ExecutionRuntimeLifecycleStates.Supported,
                PolicyObservedAtUtc: policyObservedAtUtc,
                ResolvedAtUtc: policyObservedAtUtc.AddMinutes(1)),
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
                Resolver,
                _telemetry)
            .ActivateAsync(package, warnings, errors, CancellationToken.None);
        if (!result.Success || result.LoadedPackage is not ActiveLoadedPackage loaded)
        {
            throw new InvalidOperationException(
                "Synthetic process package activation failed: " + string.Join(" | ", errors));
        }
        if (loaded.Worker is not ProcessRuntimeWorker worker || loaded.RuntimeActivationId == Guid.Empty)
        {
            throw new InvalidOperationException("Synthetic process package did not create a process worker.");
        }
        return new ActivatedSyntheticWorker(worker, loaded.RuntimeActivationId, warnings.ToArray());
    }

    internal async Task<IReadOnlyList<ExecutionRuntimeProcessLeaseV1>> ReadLeasesAsync()
    {
        if (!Directory.Exists(Layout.LeaseRootPath))
        {
            return [];
        }
        var leases = new List<ExecutionRuntimeProcessLeaseV1>();
        foreach (var path in Directory.EnumerateFiles(Layout.LeaseRootPath, "*.json")
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            leases.Add(JsonSerializer.Deserialize<ExecutionRuntimeProcessLeaseV1>(
                           await File.ReadAllBytesAsync(path),
                           ExecutionRuntimeStore.JsonOptions)
                       ?? throw new InvalidDataException($"Execution-runtime lease '{path}' was empty."));
        }
        return leases;
    }

    private async Task<IReadOnlyDictionary<string, string>> CreateLauncherPayloadAsync()
    {
        var launcherFileName = LauncherName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        var launcherSource = Path.Combine(AppContext.BaseDirectory, launcherFileName);
        var launcherAssemblySource = Path.Combine(AppContext.BaseDirectory, LauncherName + ".dll");
        if (!File.Exists(launcherSource) || !File.Exists(launcherAssemblySource))
        {
            throw new FileNotFoundException("The synthetic execution-runtime launcher build output is missing.");
        }

        var runtimeDirectory = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        var frameworkVersion = Path.GetFileName(runtimeDirectory);
        var frameworkNameRoot = Directory.GetParent(runtimeDirectory)
                                ?? throw new DirectoryNotFoundException("The current .NET framework root is unavailable.");
        var sharedRoot = frameworkNameRoot.Parent
                         ?? throw new DirectoryNotFoundException("The current .NET shared root is unavailable.");
        var dotnetRoot = sharedRoot.Parent
                         ?? throw new DirectoryNotFoundException("The current .NET root is unavailable.");
        var hostFxrDirectory = Path.Combine(dotnetRoot.FullName, "host", "fxr", frameworkVersion);
        if (!Directory.Exists(hostFxrDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The current apphost hostfxr directory '{hostFxrDirectory}' is unavailable.");
        }

        var sourcesRoot = Path.Combine(RootPath, "runtime-sources");
        Directory.CreateDirectory(sourcesRoot);
        var runtimeConfigSource = Path.Combine(sourcesRoot, LauncherName + ".runtimeconfig.json");
        await File.WriteAllTextAsync(
            runtimeConfigSource,
            JsonSerializer.Serialize(new
            {
                runtimeOptions = new
                {
                    tfm = "net10.0",
                    framework = new
                    {
                        name = "Microsoft.NETCore.App",
                        version = frameworkVersion,
                    },
                },
            }));

        var payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"payload/bin/{launcherFileName}"] = launcherSource,
            [$"payload/bin/{LauncherName}.dll"] = launcherAssemblySource,
            [$"payload/bin/{LauncherName}.runtimeconfig.json"] = runtimeConfigSource,
        };
        AddDirectory(
            payload,
            hostFxrDirectory,
            $"payload/bin/dotnet/host/fxr/{frameworkVersion}");
        AddDirectory(
            payload,
            runtimeDirectory,
            $"payload/bin/dotnet/shared/Microsoft.NETCore.App/{frameworkVersion}");
        return payload;
    }

    private async Task<ExecutionRuntimeReceiptV1> InstallAsync(
        SyntheticRuntimeKind kind,
        string runtimeVersion,
        int packagingRevision,
        IReadOnlyDictionary<string, string> commonPayload,
        string? runtimeIdentifier = null)
    {
        var runtimeId = kind == SyntheticRuntimeKind.DotNet
            ? SunderExecutionRuntimeFormat.DotNetRuntimeId
            : SunderExecutionRuntimeFormat.NodeRuntimeId;
        var moduleAbi = kind == SyntheticRuntimeKind.DotNet
            ? SunderExecutionRuntimeFormat.DotNetModuleAbi
            : SunderExecutionRuntimeFormat.NodeModuleAbi;
        var rid = runtimeIdentifier ?? PackageTargetSelection.GetCurrentRuntimeIdentifier();
        var artifactRoot = Path.Combine(
            RootPath,
            "artifacts",
            $"{runtimeId}-{runtimeVersion}-{packagingRevision}-{rid}");
        Directory.CreateDirectory(artifactRoot);
        var upstreamSha256 = Hash(Encoding.UTF8.GetBytes(runtimeId + "\n" + runtimeVersion));
        var attestation = new SunderExecutionRuntimeNativeAttestation
        {
            SchemaVersion = SunderExecutionRuntimeFormat.CurrentNativeAttestationVersion,
            RuntimeId = runtimeId,
            RuntimeVersion = runtimeVersion,
            PackagingRevision = packagingRevision,
            Rid = rid,
            UpstreamSha256 = upstreamSha256,
            Workflow = new SunderExecutionRuntimeAttestationWorkflow
            {
                Name = "sunder.synthetic.runtime",
                RunId = "signed-process-gate",
                SourceRevision = new string('a', 40),
            },
            Toolchain = new SunderExecutionRuntimeAttestationToolchain
            {
                Name = "dotnet-sdk",
                Version = "10.0.100",
            },
            LauncherSmoke = new SunderExecutionRuntimeAttestationLauncherSmoke
            {
                Result = SunderExecutionRuntimeFormat.LauncherSmokePassed,
            },
            Platform = new SunderExecutionRuntimeAttestationPlatform
            {
                Signature = rid.StartsWith("linux-", StringComparison.Ordinal)
                    ? SunderExecutionRuntimeFormat.PlatformCheckNotApplicable
                    : SunderExecutionRuntimeFormat.PlatformCheckPassed,
                Notarization = rid.StartsWith("osx-", StringComparison.Ordinal)
                    ? SunderExecutionRuntimeFormat.PlatformCheckPassed
                    : SunderExecutionRuntimeFormat.PlatformCheckNotApplicable,
            },
            Signing = new SunderExecutionRuntimeAttestationSigning
            {
                Algorithm = SunderExecutionRuntimeFormat.SigningAlgorithm,
                KeyId = AttestationKeyId,
                Signature = Convert.ToBase64String(new byte[SunderExecutionRuntimeFormat.Ed25519SignatureBytes]),
            },
        };
        var attestationBytes = SunderExecutionRuntimeCanonicalJson.SerializeNativeAttestation(attestation);
        var attestationSource = Path.Combine(artifactRoot, "attestation.json");
        await File.WriteAllBytesAsync(attestationSource, attestationBytes);
        var launcherFileName = LauncherName + (rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty);
        var manifest = new SunderExecutionRuntimeManifest
        {
            SchemaVersion = SunderExecutionRuntimeFormat.CurrentSchemaVersion,
            Id = runtimeId,
            Version = runtimeVersion,
            PackagingRevision = packagingRevision,
            Rid = rid,
            ModuleAbi = moduleAbi,
            WorkerProtocol = SunderExecutionRuntimeFormat.WorkerProtocol,
            Launch = new SunderExecutionRuntimeLaunch
            {
                Executable = $"payload/bin/{launcherFileName}",
                Arguments = kind == SyntheticRuntimeKind.DotNet
                    ?
                    [
                        SunderExecutionRuntimeFormat.DotNetExecArgument,
                        SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder,
                    ]
                    : [SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder],
            },
            ContentIndex = SunderExecutionRuntimeFormat.ContentIndexPath,
            Upstream = new SunderExecutionRuntimeUpstream
            {
                Name = kind == SyntheticRuntimeKind.DotNet ? "dotnet" : "node",
                Version = runtimeVersion,
                SourceUri = $"https://example.test/{runtimeId}/{runtimeVersion}.tar.gz",
                SourceSha256 = upstreamSha256,
            },
            Provenance = new SunderExecutionRuntimeProvenance
            {
                AttestationPath = SunderExecutionRuntimeFormat.NativeAttestationPath,
                AttestationSha256 = Hash(attestationBytes),
                AttestationKeyId = AttestationKeyId,
            },
            Licenses = ["MIT"],
            Signing = new SunderExecutionRuntimeSigning
            {
                Algorithm = SunderExecutionRuntimeFormat.SigningAlgorithm,
                KeyId = ReleaseKeyId,
                Signature = SunderExecutionRuntimeFormat.SignaturePath,
            },
        };
        var payload = new Dictionary<string, string>(commonPayload, StringComparer.Ordinal)
        {
            [SunderExecutionRuntimeFormat.NativeAttestationPath] = attestationSource,
        };
        var archivePath = Path.Combine(artifactRoot, "runtime.sunderruntime");
        await SunderExecutionRuntimeArchiveWriter.WriteAsync(
            manifest,
            archivePath,
            payload,
            new TestSigner(ReleaseKeyId, ReleaseKey));
        var artifactSha256 = await HashFileAsync(archivePath);
        var artifactSize = new FileInfo(archivePath).Length;
        var manifestSha256 = Hash(SunderExecutionRuntimeCanonicalJson.SerializeManifest(manifest));
        var plan = new ExecutionRuntimeAcquisitionPlan(
            new ExecutionRuntimeIdentity(runtimeId, runtimeVersion, packagingRevision, rid, moduleAbi),
            artifactSha256,
            artifactSize,
            manifestSha256,
            ContentIdentity: null,
            ReleaseKeyId,
            MinimumHostTrustGeneration: 1,
            ExecutionRuntimeTrustEnvironment.Production,
            RuntimeExecutionRuntimeAcquisitionSources.Registry,
            RegistryOrigin,
            manifest.Provenance.AttestationSha256,
            manifest.Provenance.AttestationKeyId);
        var installed = await Store.EnsureAsync(plan, archivePath);
        var verification = await Store.VerifyAsync(plan.Identity, deep: true);
        if (!verification.Healthy)
        {
            throw new InvalidDataException(
                $"Installed synthetic execution runtime failed deep verification: {verification.Message}");
        }
        return installed.Receipt;
    }

    private static string GetAlternateRuntimeIdentifier()
    {
        var current = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        var platformPrefix = current[..(current.IndexOf('-') + 1)];
        return SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers.First(candidate =>
            candidate.StartsWith(platformPrefix, StringComparison.Ordinal)
            && !string.Equals(candidate, current, StringComparison.Ordinal));
    }

    private static void AddDirectory(
        IDictionary<string, string> payload,
        string sourceRoot,
        string archiveRoot)
    {
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, source).Replace(Path.DirectorySeparatorChar, '/');
            payload.Add(archiveRoot + "/" + relative, source);
        }
    }

    private static SunderPackageContentIndexEntry CreatePackageContentEntry(string path, string archivePath)
    {
        using var stream = File.OpenRead(path);
        return new SunderPackageContentIndexEntry(archivePath, Hash(stream), stream.Length);
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string Hash(Stream stream)
        => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

    private static string Hash(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static byte[] CreateSignature(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key)
    {
        var signed = new byte[key.Length + input.Length];
        key.CopyTo(signed);
        input.CopyTo(signed.AsSpan(key.Length));
        return SHA512.HashData(signed);
    }

    internal sealed class TestSigner(string keyId, byte[] key) : ISunderExecutionRuntimeArchiveSigner
    {
        public string KeyId { get; } = keyId;

        public byte[] Sign(ReadOnlySpan<byte> signatureInput)
            => CreateSignature(signatureInput, key);
    }

    internal sealed class TestSignatureVerifier : ISunderExecutionRuntimeArchiveSignatureVerifier
    {
        public bool Verify(
            ReadOnlySpan<byte> signatureInput,
            ReadOnlySpan<byte> signature,
            ReadOnlySpan<byte> trustedPublicKey)
            => CryptographicOperations.FixedTimeEquals(
                signature,
                CreateSignature(signatureInput, trustedPublicKey));
    }
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
