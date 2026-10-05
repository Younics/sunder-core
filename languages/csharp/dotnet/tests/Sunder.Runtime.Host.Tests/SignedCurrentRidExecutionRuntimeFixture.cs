using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Sunder.ExecutionRuntime.Build;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class SignedCurrentRidExecutionRuntimeFixture : IAsyncLifetime
{
    private const string ReleaseKeyId = "sunder.release.test.v1";
    private const string RegistryOrigin = "https://registry.example/";
    private const string SyntheticLauncherName = "Sunder.Runtime.Host.Tests.SyntheticExecutionRuntimeLauncher";
    private static readonly byte[] ReleaseKey = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
    private static readonly byte[] AttestationKey = Enumerable.Range(33, 32).Select(static value => (byte)value).ToArray();
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly Dictionary<RuntimeBuildKey, ExecutionRuntimeReceiptV1> _receipts = [];
    private readonly Dictionary<RuntimeSourceKey, RuntimeSourceSnapshot> _sources = [];
    private readonly TestExecutionRuntimeGarbageCollectionAuthority _garbageCollectionAuthority = new();
    private ExecutionRuntimeLockDocument _fixtureLock = null!;
    private long _policyRevision;

    public SignedCurrentRidExecutionRuntimeFixture()
    {
        RootPath = Path.Combine(
            AppContext.BaseDirectory,
            ".sunder-current-rid-runtime-tests",
            Guid.NewGuid().ToString("N"));
        Layout = new ExecutionRuntimeStoreLayout(Path.Combine(RootPath, "runtime-store"));
        LifecyclePolicies = new ExecutionRuntimeLifecyclePolicyCache(Layout);
        var trust = new ExecutionRuntimeHostTrustService(
            "current-rid-host-integration",
            generation: 1,
            developmentTrustEnabled: false,
            productionKeys: [new ExecutionRuntimeHostTrustKey(ReleaseKeyId, ReleaseKey, 1)]);
        Store = new ExecutionRuntimeStore(
            Layout,
            trust,
            _garbageCollectionAuthority,
            signatureVerifier: new SignedSyntheticExecutionRuntimeFixture.TestSignatureVerifier());
        LaunchResolver = new ExecutionRuntimeLaunchResolver(
            Store,
            LifecyclePolicies,
            TimeProvider.System);
        LocalBindingResolver = new LocalExecutionRuntimeBindingResolver(
            Store,
            LifecyclePolicies,
            TimeProvider.System);
    }

    internal string RootPath { get; }
    internal ExecutionRuntimeStoreLayout Layout { get; }
    internal ExecutionRuntimeStore Store { get; }
    internal ExecutionRuntimeLifecyclePolicyCache LifecyclePolicies { get; }
    internal ExecutionRuntimeLaunchResolver LaunchResolver { get; }
    internal LocalExecutionRuntimeBindingResolver LocalBindingResolver { get; }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(RootPath);
        _fixtureLock = await ExecutionRuntimeLockLoader.LoadAsync(
            Path.Combine(AppContext.BaseDirectory, "ExecutionRuntimeFixtures", "test-fixtures.lock.json"),
            ExecutionRuntimeLockPolicy.TestFixtureKind);
        await Store.InitializeAsync();
        await LifecyclePolicies.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _buildGate.Dispose();
        _ = ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(RootPath);
        return Task.CompletedTask;
    }

    internal Task<ExecutionRuntimeReceiptV1> EnsureDotNetAsync(
        string version,
        int packagingRevision)
        => EnsureAsync(
            new RuntimeBuildKey(SyntheticRuntimeKind.DotNet, version, packagingRevision, null, FailingLauncher: false));

    internal Task<ExecutionRuntimeReceiptV1> EnsureFailingDotNetAsync(
        string version,
        int packagingRevision)
        => EnsureAsync(
            new RuntimeBuildKey(SyntheticRuntimeKind.DotNet, version, packagingRevision, null, FailingLauncher: true));

    internal Task<ExecutionRuntimeReceiptV1> EnsureNodeAsync(
        string version,
        int packagingRevision,
        string nodeExecutablePath)
        => EnsureAsync(new RuntimeBuildKey(
            SyntheticRuntimeKind.Node,
            version,
            packagingRevision,
            Path.GetFullPath(nodeExecutablePath),
            FailingLauncher: false));

    internal async Task<ExecutionRuntimeLifecyclePolicyEntryV1> ObserveSupportedAsync(
        ExecutionRuntimeReceiptV1 receipt)
    {
        var observedAtUtc = DateTimeOffset.UtcNow;
        var revision = Interlocked.Increment(ref _policyRevision);
        await LifecyclePolicies.ObserveAsync([
            new ExecutionRuntimeLifecyclePolicyObservation(
                receipt,
                RegistryOrigin,
                new RegistryExecutionRuntimeLifecycle(
                    ExecutionRuntimeLifecycleStates.Supported,
                    observedAtUtc.AddDays(-2),
                    observedAtUtc.AddDays(-1),
                    DeprecatedAtUtc: null,
                    InstallBlockedAtUtc: null,
                    ActivationBlockedAtUtc: null,
                    RevokedAtUtc: null,
                    Message: null),
                revision,
                receipt.TrustGeneration,
                observedAtUtc),
        ]);
        var snapshot = LifecyclePolicies.CaptureSnapshot();
        if (!snapshot.TryGet(receipt.Identity, out var policy) || policy is null)
        {
            throw new InvalidDataException("The current-RID runtime policy was not persisted.");
        }
        return policy;
    }

    internal static InstalledPackageExecutionRuntimeBindingRecord CreateBinding(
        ExecutionRuntimeReceiptV1 receipt,
        ExecutionRuntimeLifecyclePolicyEntryV1 policy)
        => new(
            SunderPackageFormat.RuntimeHostRole,
            receipt.Identity.Rid,
            receipt.Identity.RuntimeId,
            receipt.Identity.Version,
            receipt.Identity.PackagingRevision,
            receipt.Identity.ModuleAbi,
            receipt.ManifestSha256,
            receipt.ArtifactSha256,
            receipt.ArtifactSize,
            policy.RegistryOrigin,
            receipt.AcquisitionSource,
            receipt.SigningKeyId,
            receipt.TrustGeneration,
            policy.RuntimePolicyRevision,
            policy.Lifecycle.State,
            policy.PolicyObservedAtUtc,
            DateTimeOffset.UtcNow);

    internal string GetRuntimeRoot(ExecutionRuntimeReceiptV1 receipt)
        => Layout.GetContentRootPath(receipt.ContentIdentity);

    internal string GetLauncherPath(ExecutionRuntimeReceiptV1 receipt)
        => Path.Combine([GetRuntimeRoot(receipt), .. receipt.LauncherPath.Split('/')]);

    private async Task<ExecutionRuntimeReceiptV1> EnsureAsync(RuntimeBuildKey key)
    {
        await _buildGate.WaitAsync();
        try
        {
            if (_receipts.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var template = _fixtureLock.Entries!.Single(entry => entry is not null
                && string.Equals(
                    entry.Id,
                    key.Kind == SyntheticRuntimeKind.DotNet
                        ? SunderExecutionRuntimeFormat.DotNetRuntimeId
                        : SunderExecutionRuntimeFormat.NodeRuntimeId,
                    StringComparison.Ordinal)
                && string.Equals(
                    entry.Rid,
                    PackageTargetSelection.GetCurrentRuntimeIdentifier(),
                    StringComparison.Ordinal))!;
            var source = await GetSourceAsync(key, template);
            var entry = template with
            {
                Version = key.Version,
                PackagingRevision = key.PackagingRevision,
                Source = template.Source! with
                {
                    Sha256 = source.Sha256,
                    ArchiveFormat = ExecutionRuntimeLockPolicy.ZipArchiveFormat,
                    SourceRoot = ".",
                },
                License = template.License! with
                {
                    Files =
                    [
                        template.License.Files![0]! with { Sha256 = source.LicenseSha256 },
                    ],
                },
            };
            var artifactRoot = Path.Combine(
                RootPath,
                "artifacts",
                $"{entry.Id}-{entry.Version}-{entry.PackagingRevision}-{entry.Rid}");
            Directory.CreateDirectory(artifactRoot);
            var attestationPath = Path.Combine(artifactRoot, "native-build-attestation.v1.json");
            await WriteAttestationAsync(entry, attestationPath);
            var archivePath = Path.Combine(artifactRoot, "runtime.sunderruntime");
            await BuildSyntheticArchiveAsync(
                entry,
                source,
                attestationPath,
                archivePath);

            var inspection = await SunderExecutionRuntimeArchiveInspector.InspectAsync(
                archivePath,
                Path.Combine(artifactRoot, "validated.sunderruntime"),
                Path.Combine(artifactRoot, "inspection"),
                new SignedSyntheticExecutionRuntimeFixture.TestSignatureVerifier(),
                new SunderExecutionRuntimeTrustedKey(ReleaseKeyId, ReleaseKey),
                new SignedSyntheticExecutionRuntimeFixture.TestSignatureVerifier(),
                new SunderExecutionRuntimeTrustedKey(entry.Trust!.AttestationSigningKeyId!, AttestationKey));
            if (!inspection.Success || inspection.Manifest is null)
            {
                throw new InvalidDataException(
                    "The signed current-RID execution runtime failed inspection: "
                    + string.Join(" | ", inspection.Errors));
            }
            var manifest = inspection.Manifest;
            var plan = new ExecutionRuntimeAcquisitionPlan(
                new ExecutionRuntimeIdentity(
                    manifest.Id!,
                    manifest.Version!,
                    manifest.PackagingRevision,
                    manifest.Rid!,
                    manifest.ModuleAbi!),
                inspection.ArchiveSha256!,
                inspection.ArchiveSize,
                inspection.ManifestSha256!,
                inspection.ContentIdentity,
                ReleaseKeyId,
                MinimumHostTrustGeneration: 1,
                ExecutionRuntimeTrustEnvironment.Production,
                RuntimeExecutionRuntimeAcquisitionSources.Registry,
                RegistryOrigin,
                manifest.Provenance!.AttestationSha256!,
                manifest.Provenance.AttestationKeyId!);
            var installed = await Store.EnsureAsync(plan, archivePath);
            var verification = await Store.VerifyAsync(installed.Receipt.Identity, deep: true);
            if (!verification.Healthy)
            {
                throw new InvalidDataException(
                    "The installed current-RID execution runtime failed deep verification: "
                    + verification.Message);
            }
            _receipts.Add(key, installed.Receipt);
            return installed.Receipt;
        }
        finally
        {
            _buildGate.Release();
        }
    }

    private static async Task BuildSyntheticArchiveAsync(
        ExecutionRuntimeLockEntry entry,
        RuntimeSourceSnapshot source,
        string attestationPath,
        string archivePath)
    {
        var workingRoot = Path.Combine(
            Path.GetDirectoryName(archivePath)!,
            $"synthetic-build-{Guid.NewGuid():N}");
        try
        {
            var payloadFiles = await ExecutionRuntimeSourceMaterializer.MaterializeAsync(
                ExecutionRuntimeLockPolicy.ProductionKind,
                entry,
                source.ArchivePath,
                workingRoot);
            var attestationBytes = await File.ReadAllBytesAsync(attestationPath);
            var allPayloadFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in payloadFiles)
            {
                allPayloadFiles.Add(file.Key, file.Value);
            }
            allPayloadFiles.Add(SunderExecutionRuntimeFormat.NativeAttestationPath, attestationPath);
            var manifest = new SunderExecutionRuntimeManifest
            {
                SchemaVersion = SunderExecutionRuntimeFormat.CurrentSchemaVersion,
                Id = entry.Id,
                Version = entry.Version,
                PackagingRevision = entry.PackagingRevision,
                Rid = entry.Rid,
                ModuleAbi = entry.Profile!.ModuleAbi,
                WorkerProtocol = entry.Profile.WorkerProtocol,
                Launch = new SunderExecutionRuntimeLaunch
                {
                    Executable = entry.Profile.LaunchExecutable,
                    Arguments = entry.Profile.LaunchArguments,
                },
                ContentIndex = SunderExecutionRuntimeFormat.ContentIndexPath,
                Upstream = new SunderExecutionRuntimeUpstream
                {
                    Name = entry.Source!.Name,
                    Version = entry.Version,
                    SourceUri = entry.Source.Uri,
                    SourceSha256 = source.Sha256,
                },
                Provenance = new SunderExecutionRuntimeProvenance
                {
                    AttestationPath = SunderExecutionRuntimeFormat.NativeAttestationPath,
                    AttestationSha256 = Convert.ToHexString(SHA256.HashData(attestationBytes)).ToLowerInvariant(),
                    AttestationKeyId = entry.Trust!.AttestationSigningKeyId,
                },
                Licenses = entry.License!.Spdx,
                Signing = new SunderExecutionRuntimeSigning
                {
                    Algorithm = SunderExecutionRuntimeFormat.SigningAlgorithm,
                    KeyId = ReleaseKeyId,
                    Signature = SunderExecutionRuntimeFormat.SignaturePath,
                },
            };
            await SunderExecutionRuntimeArchiveWriter.WriteAsync(
                manifest,
                archivePath,
                allPayloadFiles,
                new SignedSyntheticExecutionRuntimeFixture.TestSigner(ReleaseKeyId, ReleaseKey));
        }
        finally
        {
            if (Directory.Exists(workingRoot))
            {
                Directory.Delete(workingRoot, recursive: true);
            }
        }
    }

    private async Task<RuntimeSourceSnapshot> GetSourceAsync(
        RuntimeBuildKey build,
        ExecutionRuntimeLockEntry template)
    {
        var sourceKey = new RuntimeSourceKey(build.Kind, build.NodeExecutablePath, build.FailingLauncher);
        if (_sources.TryGetValue(sourceKey, out var existing))
        {
            return existing;
        }

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (build.FailingLauncher)
        {
            AddFile(files, template.Recipe!.LauncherSource!, Path.Combine(
                AppContext.BaseDirectory,
                SyntheticLauncherName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)));
            AddFile(files, template.License!.Files![0]!.Path!, FindDotNetLicense());
        }
        else if (build.Kind == SyntheticRuntimeKind.DotNet)
        {
            AddDotNetFiles(files, template);
        }
        else
        {
            AddNodeFiles(
                files,
                template,
                build.NodeExecutablePath
                ?? throw new InvalidOperationException("A local Node executable is required."));
        }

        var sourceRoot = Path.Combine(RootPath, "runtime-sources");
        Directory.CreateDirectory(sourceRoot);
        var archivePath = Path.Combine(
            sourceRoot,
            $"{build.Kind}-{(build.FailingLauncher ? "failing" : HashText(build.NodeExecutablePath ?? "current"))}.zip");
        await WriteZipAsync(archivePath, files);
        var licensePath = template.License!.Files![0]!.Path!;
        var snapshot = new RuntimeSourceSnapshot(
            archivePath,
            await HashFileAsync(archivePath),
            await HashFileAsync(files[licensePath]));
        _sources.Add(sourceKey, snapshot);
        return snapshot;
    }

    private static void AddDotNetFiles(
        IDictionary<string, string> files,
        ExecutionRuntimeLockEntry template)
    {
        var runtimeDirectory = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        var frameworkVersion = Path.GetFileName(runtimeDirectory);
        var frameworkNameRoot = Directory.GetParent(runtimeDirectory)
                                ?? throw new DirectoryNotFoundException("The current .NET framework root is unavailable.");
        var sharedRoot = frameworkNameRoot.Parent
                         ?? throw new DirectoryNotFoundException("The current .NET shared root is unavailable.");
        var dotnetRoot = sharedRoot.Parent
                         ?? throw new DirectoryNotFoundException("The current .NET root is unavailable.");
        var hostFxrDirectory = Path.Combine(dotnetRoot.FullName, "host", "fxr", frameworkVersion);
        AddFile(
            files,
            template.Recipe!.LauncherSource!,
            Path.Combine(dotnetRoot.FullName, "dotnet" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)));
        AddFile(files, template.License!.Files![0]!.Path!, FindDotNetLicense());
        AddDirectory(files, hostFxrDirectory, $"host/fxr/{frameworkVersion}");
        AddDirectory(files, runtimeDirectory, $"shared/Microsoft.NETCore.App/{frameworkVersion}");
    }

    private static void AddNodeFiles(
        IDictionary<string, string> files,
        ExecutionRuntimeLockEntry template,
        string nodeExecutablePath)
    {
        AddFile(files, template.Recipe!.LauncherSource!, nodeExecutablePath);
        var license = FindAncestorFile(Path.GetDirectoryName(nodeExecutablePath)!, "LICENSE", 3)
                      ?? throw new FileNotFoundException(
                          $"Could not locate the Node license above '{nodeExecutablePath}'.");
        AddFile(files, template.License!.Files![0]!.Path!, license);
    }

    private static string FindDotNetLicense()
    {
        var runtimeDirectory = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        return FindAncestorFile(runtimeDirectory, "LICENSE.txt", 4)
               ?? throw new FileNotFoundException(
                   $"Could not locate the .NET license above '{runtimeDirectory}'.");
    }

    private static string? FindAncestorFile(string startPath, string fileName, int maximumParents)
    {
        var directory = new DirectoryInfo(startPath);
        for (var index = 0; directory is not null && index <= maximumParents; index++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static void AddDirectory(
        IDictionary<string, string> files,
        string sourceRoot,
        string archiveRoot)
    {
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"Current runtime directory '{sourceRoot}' is unavailable.");
        }
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, source).Replace(Path.DirectorySeparatorChar, '/');
            AddFile(files, archiveRoot + "/" + relative, source);
        }
    }

    private static void AddFile(IDictionary<string, string> files, string archivePath, string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Current runtime source '{sourcePath}' is unavailable.", sourcePath);
        }
        if (!files.TryAdd(archivePath, sourcePath))
        {
            throw new InvalidDataException($"Current runtime source contains duplicate path '{archivePath}'.");
        }
    }

    private static async Task WriteZipAsync(
        string archivePath,
        IReadOnlyDictionary<string, string> files)
    {
        await using var destination = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (archiveName, sourcePath) in files)
        {
            var entry = archive.CreateEntry(archiveName, CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = entry.Open();
            await source.CopyToAsync(output);
        }
    }

    private static async Task WriteAttestationAsync(
        ExecutionRuntimeLockEntry entry,
        string outputPath)
    {
        var attestation = new SunderExecutionRuntimeNativeAttestation
        {
            SchemaVersion = SunderExecutionRuntimeFormat.CurrentNativeAttestationVersion,
            RuntimeId = entry.Id,
            RuntimeVersion = entry.Version,
            PackagingRevision = entry.PackagingRevision,
            Rid = entry.Rid,
            UpstreamSha256 = entry.Source!.Sha256,
            Workflow = new SunderExecutionRuntimeAttestationWorkflow
            {
                Name = entry.Trust!.AttestationWorkflow,
                RunId = "current-rid-host-integration",
                SourceRevision = new string('c', 40),
            },
            Toolchain = new SunderExecutionRuntimeAttestationToolchain
            {
                Name = entry.Recipe!.Toolchain!.Name,
                Version = entry.Recipe.Toolchain.Version,
            },
            LauncherSmoke = new SunderExecutionRuntimeAttestationLauncherSmoke
            {
                Result = SunderExecutionRuntimeFormat.LauncherSmokePassed,
            },
            Platform = new SunderExecutionRuntimeAttestationPlatform
            {
                Signature = entry.Trust.PlatformSignature == ExecutionRuntimeLockPolicy.RequiredTrustCheck
                    ? SunderExecutionRuntimeFormat.PlatformCheckPassed
                    : SunderExecutionRuntimeFormat.PlatformCheckNotApplicable,
                Notarization = entry.Trust.Notarization == ExecutionRuntimeLockPolicy.RequiredTrustCheck
                    ? SunderExecutionRuntimeFormat.PlatformCheckPassed
                    : SunderExecutionRuntimeFormat.PlatformCheckNotApplicable,
            },
            Signing = new SunderExecutionRuntimeAttestationSigning
            {
                Algorithm = SunderExecutionRuntimeFormat.SigningAlgorithm,
                KeyId = entry.Trust.AttestationSigningKeyId,
                Signature = null,
            },
        };
        attestation = attestation with
        {
            Signing = attestation.Signing! with
            {
                Signature = Convert.ToBase64String(
                    SignedSyntheticExecutionRuntimeFixture.CreateSignature(
                        SunderExecutionRuntimeNativeAttestationSignatures.CreateSignatureInput(attestation),
                        AttestationKey)),
            },
        };
        await File.WriteAllBytesAsync(
            outputPath,
            SunderExecutionRuntimeCanonicalJson.SerializeNativeAttestation(attestation));
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..12];

    private sealed record RuntimeBuildKey(
        SyntheticRuntimeKind Kind,
        string Version,
        int PackagingRevision,
        string? NodeExecutablePath,
        bool FailingLauncher);

    private sealed record RuntimeSourceKey(
        SyntheticRuntimeKind Kind,
        string? NodeExecutablePath,
        bool FailingLauncher);

    private sealed record RuntimeSourceSnapshot(
        string ArchivePath,
        string Sha256,
        string LicenseSha256);
}
