using System.Runtime.InteropServices;
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
    public void Validate_WhenSelectedTargetRequiresRpcV1_AcceptsCanonicalCapability()
    {
        var errors = SunderSdkCompatibilityProfile.Validate(
            "test.package",
            RuntimeTargetKey,
            CreateTarget(capabilities:
            [
                SunderSdkCapabilities.Baseline11V1,
                SunderSdkCapabilities.RpcV1,
            ]));

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("storage.key-migration.v2")]
    [InlineData("runtime-invocation-errors.v2")]
    [InlineData("view-navigation-preparation.v2")]
    [InlineData("rpc.v3")]
    [InlineData("worker-protocol.v3")]
    public void Validate_WhenSelectedTargetCapabilityIsUnknown_RejectsBeforeActivation(string capability)
    {
        var errors = SunderSdkCompatibilityProfile.Validate(
            "test.package",
            RuntimeTargetKey,
            CreateTarget(capabilities: ["sdk-baseline-1-1.v1", capability]));

        Assert.Contains(errors, error => error.Contains($"Host capability '{capability}'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenRuntimeWorkerRequiresProtocolV1_AcceptsExactOptIn()
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
                    SunderSdkCapabilities.RpcV1,
                    SunderWorkerProtocol.V1Capability,
                ]));

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenV1WorkerRequiresReservedCapability_RejectsBeforeProcessLaunch()
    {
        string[] reservedCapabilities =
        [
            "runtime-operations.v1",
            "runtime-invocation-errors.v1",
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
                        SunderWorkerProtocol.V1Capability,
                        capability,
                    ]));

            var error = Assert.Single(errors);
            Assert.Contains($"Host capability '{capability}'", error, StringComparison.Ordinal);
            Assert.True(
                error.Contains("this Host does not support", StringComparison.Ordinal)
                || error.Contains("Worker Protocol V1 does not implement", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Validate_WhenManagedTargetRequiresWorkerProtocolV1_RejectsBeforeActivation()
    {
        var errors = SunderSdkCompatibilityProfile.Validate(
            "test.package",
            RuntimeTargetKey,
            CreateTarget(
                kind: "dotnet",
                capabilities: ["sdk-baseline-1-1.v1", "worker-protocol.v1"]));

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

            var prepared = await new PackageSessionPreparer(
                currentRid,
                ephemeralRuntimeBindingResolver: TestExecutionRuntimeLaunchResolver.Instance).PrepareDevPackageAsync(
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

            var prepared = await new PackageSessionPreparer(
                currentRid,
                ephemeralRuntimeBindingResolver: TestExecutionRuntimeLaunchResolver.Instance).PrepareDevPackageAsync(
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
    public async Task PrepareDevPackageAsync_WhenRuntimeTargetKindIsWorker_ProjectsNonExecutableModule()
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

            var prepared = await new PackageSessionPreparer(
                currentRid,
                ephemeralRuntimeBindingResolver: TestExecutionRuntimeLaunchResolver.Instance).PrepareDevPackageAsync(
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
                Assert.False((mode & (UnixFileMode.UserExecute
                                      | UnixFileMode.GroupExecute
                                      | UnixFileMode.OtherExecute)) != 0);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CanonicalWorkerFixture_UsesOnlyThinFrameworkDependentPayload()
    {
        var root = CreateTempDirectory();
        var currentRid = RuntimeInformation.RuntimeIdentifier;
        try
        {
            CanonicalPackageTestBuilder.WriteExplodedPackage(
                root,
                "test.thin.worker",
                "1.0.0",
                typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
                roles: [SunderPackageFormat.RuntimeHostRole],
                runtimeIdentifiers: [currentRid]);

            var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(root);

            Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
            var target = Assert.Single(validation.Manifest!.Targets ?? [])!;
            Assert.Equal("lib/Sunder.Runtime.Host.TestWorker.dll", target.EntryPoint);
            Assert.Equal("sunder.runtime.dotnet", target.ExecutionRuntime!.Id);
            Assert.Equal(">=10.0.0 <11.0.0", target.ExecutionRuntime.VersionRange);
            Assert.Equal("sunder.dotnet-worker-module.v1", target.ExecutionRuntime.ModuleAbi);
            var payloadFiles = Directory.EnumerateFiles(
                    Path.Combine(root, "payload", "runtime", currentRid, "lib"))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                new[]
                {
                    "Sunder.Runtime.Host.TestWorker.deps.json",
                    "Sunder.Runtime.Host.TestWorker.dll",
                    "Sunder.Runtime.Host.TestWorker.runtimeconfig.json",
                },
                payloadFiles);
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
    public async Task ExternalNodeMetadata_IsIgnoredAndCannotOverrideGenericLaunch()
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
            var metadataPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)) + ".sunder-node-dev.json";
            await File.WriteAllTextAsync(metadataPath, "{\"nodePath\":\"/ambient/node\"}");

            var devErrors = new List<string>();
            var dev = await new PackageSessionPreparer(
                currentRid,
                ephemeralRuntimeBindingResolver: TestExecutionRuntimeLaunchResolver.Instance).PrepareDevPackageAsync(
                0,
                source,
                Path.Combine(root, "dev-session"),
                devErrors,
                CancellationToken.None);
            Assert.NotNull(dev);
            Assert.Empty(devErrors);
            Assert.NotNull(dev.ExecutionRuntimeBinding);
            Assert.DoesNotContain("ambient", dev.EntryAssemblyPath!, StringComparison.Ordinal);
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
                ? ["sdk-baseline-1-1.v1", "core.v1", "rpc.v1", "worker-protocol.v1"]
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
