using System.Runtime.InteropServices;
using Sunder.Package.Format;
using Sunder.Package.Hosting;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Compatibility;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimePackageManifestValidatorTests
{
    private static readonly SunderPackageTargetKey RuntimeTargetKey = new("runtime", "win-x64");

    [Fact]
    public void MultipleSupportedProfiles_AcceptEachMajorWithoutMixingTheirCapabilities()
    {
        var current = Assert.Single(SunderSdkCompatibilityProfile.SupportedProfiles, profile => profile.Role == "runtime");
        var next = current with
        {
            SdkMajors = new HashSet<int> { 2 },
            HostCapabilities = current.HostCapabilities.Append("future.v1").ToHashSet(StringComparer.Ordinal),
        };
        PackageImplementationProfile[] profiles = [current, next];

        Assert.Empty(SunderSdkCompatibilityProfile.Validate("old.package", RuntimeTargetKey, CreateTarget(), profiles));
        Assert.Empty(SunderSdkCompatibilityProfile.Validate("new.package", RuntimeTargetKey,
            CreateTarget(sdkVersion: "2.3.1", capabilities: ["worker-protocol.v1", "future.v1"]), profiles));
        Assert.NotEmpty(SunderSdkCompatibilityProfile.Validate("mixed.package", RuntimeTargetKey,
            CreateTarget(capabilities: ["worker-protocol.v1", "future.v1"]), profiles));
        var split = current with { HostCapabilities = new HashSet<string> { "worker-protocol.v1", "future.v1" } };
        Assert.NotEmpty(SunderSdkCompatibilityProfile.Validate("split.package", RuntimeTargetKey,
            CreateTarget(capabilities: ["worker-protocol.v1", "rpc.v1", "future.v1"]), [current, split]));
        // Retirement is explicit: removing the old profile stops admitting its
        // targets; it does not reinterpret them as the newer SDK line.
        Assert.NotEmpty(SunderSdkCompatibilityProfile.Validate("retired.package", RuntimeTargetKey, CreateTarget(), [next]));
        Assert.Empty(SunderSdkCompatibilityProfile.Validate("current.package", RuntimeTargetKey,
            CreateTarget(sdkVersion: "2.3.1", capabilities: ["worker-protocol.v1", "future.v1"]), [next]));
    }

    [Fact]
    public void ConsumerRequirementsCapability_RejectsOnAnOlderProfileBeforeActivation()
    {
        var current = Assert.Single(SunderSdkCompatibilityProfile.SupportedProfiles, profile => profile.Role == "runtime");
        var older = current with
        {
            HostCapabilities = current.HostCapabilities.Where(value => value != SunderSdkCapabilities.RpcConsumerRequirementsV1).ToHashSet(StringComparer.Ordinal),
        };
        var target = CreateTarget(capabilities: ["worker-protocol.v1", SunderSdkCapabilities.RpcConsumerRequirementsV1]);
        Assert.Empty(SunderSdkCompatibilityProfile.Validate("consumer.package", RuntimeTargetKey, target, [current]));
        Assert.NotEmpty(SunderSdkCompatibilityProfile.Validate("consumer.package", RuntimeTargetKey, target, [older]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_WhenSelectedTargetIsCompatible_ReturnsNoErrors(bool appStateUpdates)
    {
        var errors = SunderSdkCompatibilityProfile.Validate(
            "test.package",
            appStateUpdates ? new("app", "win-x64") : RuntimeTargetKey,
            appStateUpdates
                ? CreateTarget(role: "app", kind: "avalonia", capabilities:
                    ["app-bridge.v1", SunderSdkCapabilities.AppStateUpdatesV1])
                : CreateTarget());

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenSelectedTargetSdkVersionIsUnsupported_ReturnsCompatibilityError()
    {
        var errors = SunderSdkCompatibilityProfile.Validate(
            "test.package",
            RuntimeTargetKey,
            CreateTarget(sdkVersion: "2.0.0"));

        Assert.Contains(errors, error => error.Contains("supported SDK majors [1]", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("2.0.0", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("net10.0", "12.1.0", true)]
    [InlineData("net10.0", "12.1.4", true)]
    [InlineData("net10.0", "12.1.5", false)]
    [InlineData("net10.0", "11.3.0", false)]
    [InlineData("net10.0", "13.0.0", false)]
    [InlineData("net11.0", "12.1.4", false)]
    public void ManagedAppAdmission_RequiresImplementedSharedFrameworks(string framework, string avalonia, bool accepted)
    {
        var target = new SunderPackageTargetManifest
        {
            Role = "app", Rid = "win-x64", Kind = "avalonia", EntryPoint = "lib/App.dll",
            SdkVersion = "1.0.0", TargetFramework = framework, AvaloniaVersion = avalonia,
            RequiredHostCapabilities = ["app-bridge.v1"],
        };
        Assert.Equal(accepted, SunderSdkCompatibilityProfile.Validate("test.package", new("app", "win-x64"), target).Count == 0);
    }

    [Fact]
    public void Validate_WhenSelectedTargetRequiresRpcV1_AcceptsCanonicalCapability()
    {
        var errors = SunderSdkCompatibilityProfile.Validate(
            "test.package",
            RuntimeTargetKey,
            CreateTarget(capabilities:
            [
                "worker-protocol.v1",
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
            CreateTarget(capabilities: ["worker-protocol.v1", capability]));

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
            SunderSdkCapabilities.PackageDevelopmentV1,
            SunderSdkCapabilities.PackageLogNavigationV1,
            SunderSdkCapabilities.AppStateUpdatesV1,
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
                        SunderWorkerProtocol.V1Capability,
                        capability,
                    ]));

            var error = Assert.Single(errors);
            Assert.Contains($"Host capability '{capability}'", error, StringComparison.Ordinal);
            Assert.Contains("not implemented by 'runtime/worker'", error, StringComparison.Ordinal);
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
                capabilities: ["worker-protocol.v1"]));

        Assert.Contains(errors, error => error.Contains("no supported SDK/protocol profile", StringComparison.Ordinal));
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
        string sdkVersion = "1.0.0",
        IReadOnlyList<string?>? capabilities = null)
        => new()
        {
            Role = role,
            Rid = rid,
            Kind = kind,
            EntryPoint = "lib/Test.Package.dll",
            TargetFramework = "net10.0",
            SdkVersion = sdkVersion,
            AvaloniaVersion = kind == "avalonia" ? "12.1.4" : null,
            RequiredHostCapabilities = capabilities ?? (role == SunderPackageFormat.RuntimeHostRole
                ? ["core.v1", "rpc.v1", "worker-protocol.v1"]
                : ["app-bridge.v1", "core.v1"]),
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
