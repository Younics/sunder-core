using System.Runtime.InteropServices;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Compatibility;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimePackageManifestValidatorTests
{
    private static readonly SunderPackageTargetKey RuntimeTargetKey = new("runtime", "win-x64");

    [Fact]
    public void Validate_WhenSelectedTargetIsCompatible_ReturnsNoErrors()
    {
        var errors = SunderSdkCompatibilityProfile.Validate(
            "test.package",
            RuntimeTargetKey,
            CreateTarget());

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenSelectedTargetSdkVersionIsUnsupported_ReturnsCompatibilityError()
    {
        var errors = SunderSdkCompatibilityProfile.Validate(
            "test.package",
            RuntimeTargetKey,
            CreateTarget(sdkVersion: "2.0.0"));

        Assert.Contains(errors, error => error.Contains("target 'runtime/win-x64'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("2.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenSelectedTargetRequiresRpcV2_AcceptsWithoutRpcV1()
    {
        var errors = SunderSdkCompatibilityProfile.Validate(
            "test.package",
            RuntimeTargetKey,
            CreateTarget(capabilities:
            [
                SunderSdkCapabilities.Baseline11V1,
                SunderSdkCapabilities.RpcV2,
            ]));

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("storage.key-migration.v2")]
    [InlineData("runtime-invocation-errors.v2")]
    [InlineData("view-navigation-preparation.v2")]
    [InlineData("rpc.v3")]
    public void Validate_WhenSelectedTargetCapabilityIsUnknown_RejectsBeforeActivation(string capability)
    {
        var errors = SunderSdkCompatibilityProfile.Validate(
            "test.package",
            RuntimeTargetKey,
            CreateTarget(capabilities: ["sdk-baseline-1-1.v1", capability]));

        Assert.Contains(errors, error => error.Contains($"Host capability '{capability}'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenRuntimeWorkerRequiresProtocolV3_AcceptsExactOptIn()
    {
        var errors = SunderSdkCompatibilityProfile.Validate(
            "test.package",
            RuntimeTargetKey,
            CreateTarget(
                capabilities:
                [
                    SunderSdkCapabilities.Baseline11V1,
                    SunderSdkCapabilities.BackgroundServicesV1,
                    SunderSdkCapabilities.ContributionsV1,
                    SunderSdkCapabilities.RuntimeGenerationsV1,
                    SunderSdkCapabilities.SettingsSchemaV1,
                    SunderSdkCapabilities.SettingsV1,
                    SunderSdkCapabilities.StorageV1,
                    SunderSdkCapabilities.SecretsV1,
                    SunderSdkCapabilities.LoggingV1,
                    SunderSdkCapabilities.RpcV2,
                    SunderWorkerProtocol.V3Capability,
                ]));

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenV3WorkerRequiresReservedCapability_RejectsBeforeProcessLaunch()
    {
        string[] reservedCapabilities =
        [
            "runtime-operations.v1",
            "runtime-invocation-errors.v1",
            SunderSdkCapabilities.RpcV1,
        ];

        foreach (var capability in reservedCapabilities)
        {
            var errors = SunderSdkCompatibilityProfile.Validate(
                "test.package",
                RuntimeTargetKey,
                CreateTarget(
                    capabilities:
                    [
                        SunderSdkCapabilities.Baseline11V1,
                        SunderWorkerProtocol.V3Capability,
                        capability,
                    ]));

            var error = Assert.Single(errors);
            Assert.Contains($"Host capability '{capability}'", error, StringComparison.Ordinal);
            Assert.True(
                error.Contains("this Host does not support", StringComparison.Ordinal)
                || error.Contains("Worker Protocol V3 does not implement", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Validate_WhenManagedTargetRequiresWorkerProtocolV3_RejectsBeforeActivation()
    {
        var errors = SunderSdkCompatibilityProfile.Validate(
            "test.package",
            RuntimeTargetKey,
            CreateTarget(
                kind: "dotnet",
                capabilities: ["sdk-baseline-1-1.v1", "worker-protocol.v3"]));

        Assert.Contains(errors, error => error.Contains("valid only for Runtime worker targets", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DoesNotApplyAnotherTargetsCompatibilityMetadata()
    {
        var manifest = new SunderPackageManifest
        {
            Targets =
            [
                CreateTarget(),
                CreateTarget(
                    role: "app",
                    rid: "linux-x64",
                    kind: "avalonia",
                    sdkVersion: "2.0.0"),
            ],
        };
        Assert.True(SunderPackageTargetResolver.TryResolveTarget(manifest, RuntimeTargetKey, out var selected));

        var errors = SunderSdkCompatibilityProfile.Validate("test.package", RuntimeTargetKey, selected!);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task PrepareDevPackageAsync_SelectsExactCurrentRuntimeTargetAndIsolatesProjection()
    {
        var root = CreateTempDirectory();
        var source = Path.Combine(root, "source");
        var currentRid = RuntimeInformation.RuntimeIdentifier;
        var alternateRid = GetAlternateRid(currentRid);
        try
        {
            CanonicalPackageTestBuilder.WriteExplodedPackage(
                source,
                "test.package",
                "1.0.0",
                typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
                runtimeIdentifiers: [currentRid, alternateRid]);
            WriteTargetFile(source, "runtime", currentRid, "runtime-current.txt", currentRid);
            WriteTargetFile(source, "runtime", alternateRid, "runtime-other.txt", alternateRid);
            WriteTargetFile(source, "app", currentRid, "app-only.txt", "app");
            CanonicalPackageTestBuilder.WriteContentIndex(source);
            var errors = new List<string>();

            var prepared = await new PackageSessionPreparer(currentRid).PrepareDevPackageAsync(
                0,
                source,
                Path.Combine(root, "session"),
                errors,
                CancellationToken.None);

            Assert.NotNull(prepared);
            Assert.Empty(errors);
            Assert.Equal(new SunderPackageTargetKey(SunderPackageFormat.RuntimeHostRole, currentRid), prepared.SelectedTargetKey);
            Assert.Equal(SunderPackageFormat.WorkerTargetKind, prepared.SelectedTarget?.Kind);
            Assert.True(File.Exists(Path.Combine(prepared.ShadowFolder, "runtime-current.txt")));
            Assert.False(File.Exists(Path.Combine(prepared.ShadowFolder, "runtime-other.txt")));
            Assert.False(File.Exists(Path.Combine(prepared.ShadowFolder, "app-only.txt")));
            Assert.True(File.Exists(Path.Combine(source, "payload", "runtime", alternateRid, "runtime-other.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareDevPackageAsync_WhenExactRuntimeTargetIsMissing_DoesNotFallback()
    {
        var root = CreateTempDirectory();
        var currentRid = RuntimeInformation.RuntimeIdentifier;
        try
        {
            CanonicalPackageTestBuilder.WriteExplodedPackage(
                Path.Combine(root, "source"),
                "test.package",
                "1.0.0",
                typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
                roles: [SunderPackageFormat.RuntimeHostRole],
                runtimeIdentifiers: [GetAlternateRid(currentRid)]);
            var errors = new List<string>();

            var prepared = await new PackageSessionPreparer(currentRid).PrepareDevPackageAsync(
                0,
                Path.Combine(root, "source"),
                Path.Combine(root, "session"),
                errors,
                CancellationToken.None);

            Assert.Null(prepared);
            Assert.Contains(errors, error => error.Contains($"runtime/{currentRid}", StringComparison.Ordinal)
                                             && error.Contains("fallback", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareDevPackageAsync_WhenRuntimeTargetKindIsWorker_SelectsExecutableAdapter()
    {
        var root = CreateTempDirectory();
        var currentRid = RuntimeInformation.RuntimeIdentifier;
        try
        {
            CanonicalPackageTestBuilder.WriteExplodedPackage(
                Path.Combine(root, "source"),
                "test.package",
                "1.0.0",
                typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
                roles: [SunderPackageFormat.RuntimeHostRole],
                runtimeIdentifiers: [currentRid],
                runtimeTargetKind: SunderPackageFormat.WorkerTargetKind);
            var errors = new List<string>();

            var prepared = await new PackageSessionPreparer(currentRid).PrepareDevPackageAsync(
                0,
                Path.Combine(root, "source"),
                Path.Combine(root, "session"),
                errors,
                CancellationToken.None);

            Assert.NotNull(prepared);
            Assert.Empty(errors);
            Assert.Equal(SunderPackageFormat.WorkerTargetKind, prepared.SelectedTarget?.Kind);
            Assert.NotNull(prepared.EntryAssemblyPath);
            Assert.True(File.Exists(prepared.EntryAssemblyPath));
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(prepared.EntryAssemblyPath);
                Assert.True((mode & UnixFileMode.UserExecute) != 0);
                Assert.False((mode & (UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("process")]
    public async Task PrepareDevPackageAsync_WhenRuntimeTargetKindIsLegacy_RejectsBeforeLaunch(string kind)
    {
        var root = CreateTempDirectory();
        var source = Path.Combine(root, "source");
        try
        {
            CanonicalPackageTestBuilder.WriteExplodedPackage(
                source,
                "test.package",
                "1.0.0",
                typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
                roles: [SunderPackageFormat.RuntimeHostRole],
                runtimeTargetKind: kind);
            var errors = new List<string>();

            var prepared = await new PackageSessionPreparer().PrepareDevPackageAsync(
                0,
                source,
                Path.Combine(root, "session"),
                errors,
                CancellationToken.None);

            Assert.Null(prepared);
            Assert.Contains(errors, error => error.Contains($"kind '{kind}'", StringComparison.Ordinal)
                                             && error.Contains("invalid for role 'runtime'", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExternalNodeMetadata_IsContentBoundAndDevOnly()
    {
        var root = CreateTempDirectory();
        var source = Path.Combine(root, "source");
        var currentRid = RuntimeInformation.RuntimeIdentifier;
        try
        {
            CanonicalPackageTestBuilder.WriteExplodedPackage(
                source,
                "test.package",
                "1.0.0",
                typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
                roles: [SunderPackageFormat.RuntimeHostRole],
                runtimeIdentifiers: [currentRid],
                runtimeTargetKind: SunderPackageFormat.WorkerTargetKind);
            var record = CanonicalPackageTestBuilder.CreateInstalledRecord(source, "test.package", "1.0.0");
            var manifest = JsonSerializer.Deserialize<SunderPackageManifest>(await File.ReadAllTextAsync(
                Path.Combine(source, "manifest", "sunder-package.json")))!;
            Assert.True(SunderPackageTargetResolver.TryResolveTarget(
                manifest,
                new SunderPackageTargetKey(SunderPackageFormat.RuntimeHostRole, currentRid),
                out var target));
            var entryPoint = target!.EntryPoint;
            var metadataPath = DevProcessLaunchMetadata.GetPath(source);
            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                kind = "node",
                packageId = "test.package",
                packageVersion = "1.0.0",
                rid = currentRid,
                entryPoint,
                nodePath = typeof(RuntimePackageManifestValidatorTests).Assembly.Location,
                nodeVersion = "24.0.0",
                contentIdentity = record.ContentIdentity,
            }));

            var devErrors = new List<string>();
            var dev = await new PackageSessionPreparer(currentRid).PrepareDevPackageAsync(
                0,
                source,
                Path.Combine(root, "dev-session"),
                devErrors,
                CancellationToken.None);
            Assert.NotNull(dev);
            Assert.Empty(devErrors);
            Assert.NotNull(dev.DevProcessLaunch);

            var installedErrors = new List<string>();
            var installed = await new PackageSessionPreparer(currentRid).PrepareInstalledPackageAsync(
                0,
                record,
                source,
                Path.Combine(root, "installed-session"),
                installedErrors,
                CancellationToken.None);
            Assert.Null(installed);
            Assert.Contains(installedErrors, error => error.Contains("forbidden external Node dev metadata", StringComparison.Ordinal));

            var stale = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(metadataPath))!;
            stale["contentIdentity"] = JsonSerializer.SerializeToElement(new string('0', 64));
            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(stale));
            var staleErrors = new List<string>();
            Assert.Null(await new PackageSessionPreparer(currentRid).PrepareDevPackageAsync(
                0,
                source,
                Path.Combine(root, "stale-session"),
                staleErrors,
                CancellationToken.None));
            Assert.Contains(staleErrors, error => error.Contains("does not exactly bind", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Constructor_WhenRuntimeIdentifierIsUnsupported_RejectsIt()
        => Assert.Throws<ArgumentException>(() => new PackageSessionPreparer("browser-wasm"));

    private static SunderPackageTargetManifest CreateTarget(
        string role = "runtime",
        string rid = "win-x64",
        string kind = SunderPackageFormat.WorkerTargetKind,
        string sdkVersion = "1.1.0",
        IReadOnlyList<string?>? capabilities = null)
        => new()
        {
            Role = role,
            Rid = rid,
            Kind = kind,
            EntryPoint = "lib/Test.Package.dll",
            TargetFramework = "net10.0",
            SdkVersion = sdkVersion,
            RequiredHostCapabilities = capabilities ?? (role == SunderPackageFormat.RuntimeHostRole
                ? ["sdk-baseline-1-1.v1", "core.v1", "rpc.v2", "worker-protocol.v3"]
                : ["sdk-baseline-1-1.v1", "core.v1"]),
        };

    private static string GetAlternateRid(string currentRid)
        => SunderPackageFormat.SupportedRuntimeIdentifiers.First(rid => !string.Equals(rid, currentRid, StringComparison.Ordinal));

    private static void WriteTargetFile(
        string root,
        string role,
        string rid,
        string fileName,
        string content)
    {
        var folder = Path.Combine(root, "payload", role, rid);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, fileName), content);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-runtime-target-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
