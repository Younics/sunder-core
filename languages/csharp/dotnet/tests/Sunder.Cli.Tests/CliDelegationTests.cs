using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Cli;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Compatibility;

namespace Sunder.Cli.Tests;

public sealed class CliDelegationTests
{
    [Fact]
    public async Task Auth_login_polls_runtime_without_receiving_credentials()
    {
        var polls = 0;
        var browser = new FakeBrowserLauncher();
        var runtime = new FakeRuntimeClient
        {
            AuthStart = (_, _) => Task.FromResult(new RuntimeRegistryAuthStartResponse(
                "session-1", "https://registry.test/", "https://registry.test/login?code=public", DateTimeOffset.UtcNow.AddMinutes(1))),
            AuthSession = (_, _) => Task.FromResult<RuntimeRegistryAuthSessionStatus?>(++polls == 1
                ? new("session-1", "https://registry.test/", RuntimeRegistryAuthSessionState.Pending, null, null, RuntimeRegistryErrorCode.None, null)
                : new("session-1", "https://registry.test/", RuntimeRegistryAuthSessionState.Succeeded,
                    new RuntimeRegistryUser("user-1", "User", null, "tester", null), DateTimeOffset.UnixEpoch, RuntimeRegistryErrorCode.None, null)),
        };
        var result = await CliTestHost.RunAsync(["registry", "auth", "login"], runtime, browser: browser);
        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(2, polls);
        Assert.Equal("https://registry.test/login?code=public", browser.Opened?.AbsoluteUri);
        Assert.DoesNotContain("token", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Registry_install_is_delegated_as_one_runtime_request()
    {
        RuntimeRegistryPackageRequest? captured = null;
        var runtime = new FakeRuntimeClient
        {
            InstallRegistry = (request, _) =>
            {
                captured = request;
                return Task.FromResult(Success("Installed."));
            },
        };
        var result = await CliTestHost.RunAsync(
            ["package", "install", "demo", "--version", "1.2.3", "--allow-downgrade", "--reinstall"], runtime);
        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("demo", captured?.PackageId);
        Assert.Equal("1.2.3", captured?.Version);
        Assert.Null(captured?.Tag);
        Assert.True(captured?.AllowDowngrade);
        Assert.True(captured?.Reinstall);
    }

    [Fact]
    public async Task Registry_install_approval_preflights_and_forwards_exact_sensitive_capability_fences()
    {
        var consent = new PackageCapabilityConsent(
            "demo",
            "1.2.3",
            new string('a', 64),
            ["docker-environment.v1"]);
        RuntimeRegistryPackageRequest? captured = null;
        var preflightCalls = 0;
        var runtime = new FakeRuntimeClient
        {
            ResolveRegistry = (request, _) =>
            {
                preflightCalls++;
                Assert.Equal("demo", Assert.Single(request.Packages).PackageId);
                return Task.FromResult(new RuntimeRegistryResolveInstallPlanResponse(
                    true, [], [], [], [], [], [consent]));
            },
            InstallRegistry = (request, _) =>
            {
                captured = request;
                return Task.FromResult(Success("Installed."));
            },
        };

        var result = await CliTestHost.RunAsync([
            "package", "install", "demo", "--version", "1.2.3", "--approve-sensitive-capabilities",
        ], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(1, preflightCalls);
        Assert.Equal([consent], captured?.CapabilityConsents);
        Assert.Contains("Approved sensitive capability fences", result.Output, StringComparison.Ordinal);
        Assert.Contains(consent.ManifestSha256, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registry_install_without_approval_surfaces_runtime_consent_requirement()
    {
        var consent = new PackageCapabilityConsent(
            "demo",
            "1.2.3",
            new string('b', 64),
            ["docker-environment.v1"]);
        RuntimeRegistryPackageRequest? captured = null;
        var runtime = new FakeRuntimeClient
        {
            ResolveRegistry = (_, _) => throw new InvalidOperationException("Preflight requires explicit approval."),
            InstallRegistry = (request, _) =>
            {
                captured = request;
                return Task.FromResult(new RuntimeRegistryPackageChangeResult(
                    false,
                    RuntimeRegistryErrorCode.ConsentRequired,
                    "Sensitive capability consent is required.",
                    false,
                    false,
                    [],
                    ["Sensitive capability consent is required."],
                    [],
                    [],
                    [consent]));
            },
        };

        var result = await CliTestHost.RunAsync([
            "package", "install", "demo", "--version", "1.2.3",
        ], runtime);

        Assert.Equal(CliExitCodes.Conflict, result.ExitCode);
        Assert.Null(captured?.CapabilityConsents);
        Assert.Contains("Required sensitive capability consent", result.Output, StringComparison.Ordinal);
        Assert.Contains("--approve-sensitive-capabilities", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_upgrade_inherits_sensitive_capabilities_without_redundant_consent(bool approve)
    {
        var package = CreateSensitivePackageArchive();
        var applyCalls = 0;
        var runtime = new FakeRuntimeClient
        {
            Installed = _ => Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>([
                InstalledPackage("demo.sensitive", [SunderSdkCapabilities.DockerEnvironmentV1]),
            ]),
            ApplyLocal = (_, packageId, _, _, _) =>
            {
                applyCalls++;
                Assert.Equal("demo.sensitive", packageId);
                return Task.FromResult(PackageSuccess("Updated."));
            },
            ApplyLocalWithConsent = (_, _, _, _, _, _) =>
                throw new InvalidOperationException("Inherited capabilities must not send redundant consent."),
        };
        try
        {
            var arguments = new List<string> { "package", "install", "--file", package.ArchivePath };
            if (approve) arguments.Add("--approve-sensitive-capabilities");

            var result = await CliTestHost.RunAsync(arguments.ToArray(), runtime);

            Assert.Equal(CliExitCodes.Success, result.ExitCode);
            Assert.Equal(1, applyCalls);
            Assert.DoesNotContain("Required sensitive capability consent", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(package.RootPath, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_upgrade_requires_exact_manifest_fenced_consent_only_for_escalation(bool approve)
    {
        var package = CreateSensitivePackageArchive();
        PackageCapabilityConsent? captured = null;
        var runtime = new FakeRuntimeClient
        {
            Installed = _ => Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>([
                InstalledPackage("demo.sensitive", []),
            ]),
            ApplyLocal = (_, _, _, _, _) =>
                throw new InvalidOperationException("An unapproved escalation must not be applied."),
            ApplyLocalWithConsent = (_, _, _, _, consent, _) =>
            {
                captured = consent;
                return Task.FromResult(PackageSuccess("Updated."));
            },
        };
        try
        {
            var arguments = new List<string> { "package", "install", "--file", package.ArchivePath };
            if (approve) arguments.Add("--approve-sensitive-capabilities");

            var result = await CliTestHost.RunAsync(arguments.ToArray(), runtime);

            Assert.Equal(approve ? CliExitCodes.Success : CliExitCodes.Conflict, result.ExitCode);
            if (!approve)
            {
                Assert.Null(captured);
                Assert.Contains("Required sensitive capability consent", result.Output, StringComparison.Ordinal);
                return;
            }
            var consent = Assert.IsType<PackageCapabilityConsent>(captured);
            Assert.Equal("demo.sensitive", consent.PackageId);
            Assert.Equal("2.0.0", consent.PackageVersion);
            Assert.Equal(package.ManifestSha256, consent.ManifestSha256);
            Assert.Equal([SunderSdkCapabilities.DockerEnvironmentV1], consent.Capabilities);
        }
        finally
        {
            Directory.Delete(package.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task Update_is_delegated_to_runtime_without_registry_queries()
    {
        RuntimeRegistryUpdateRequest? captured = null;
        var runtime = new FakeRuntimeClient
        {
            UpdateRegistry = (request, _) =>
            {
                captured = request;
                return Task.FromResult(Success("Updated."));
            },
        };
        var result = await CliTestHost.RunAsync(["package", "update", "demo", "--include-prerelease"], runtime);
        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("demo", captured?.PackageId);
        Assert.True(captured?.IncludePrerelease);
        Assert.Null(captured?.RegistryOrigin);
    }

    [Fact]
    public async Task Explicit_all_update_is_delegated_as_an_unscoped_runtime_request()
    {
        RuntimeRegistryUpdateRequest? captured = null;
        var runtime = new FakeRuntimeClient
        {
            UpdateRegistry = (request, _) =>
            {
                captured = request;
                return Task.FromResult(Success("Updated."));
            },
        };

        var result = await CliTestHost.RunAsync(["package", "update", "--all"], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Null(captured?.PackageId);
        Assert.Null(captured?.RegistryOrigin);
    }

    [Fact]
    public async Task Update_uses_an_origin_filter_only_when_explicitly_requested()
    {
        RuntimeRegistryUpdateRequest? captured = null;
        var runtime = new FakeRuntimeClient
        {
            UpdateRegistry = (request, _) =>
            {
                captured = request;
                return Task.FromResult(Success("Updated."));
            },
        };

        var result = await CliTestHost.RunAsync([
            "package", "update", "demo", "--registry-origin", "https://other.example/",
        ], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("https://other.example/", captured?.RegistryOrigin);
    }

    [Fact]
    public async Task Update_requires_runtime_configuration_only()
    {
        CliConfigurationRequirements? requirements = null;
        var runtime = new FakeRuntimeClient
        {
            UpdateRegistry = (_, _) => Task.FromResult(Success("Updated.")),
        };

        var result = await CliTestHost.RunAsync(
            ["package", "update", "--all"],
            runtime,
            optionsFactory: (_, selectedRequirements) =>
            {
                requirements = selectedRequirements;
                return new CliOptions(
                    RegistryApiUrl: null,
                    RegistryWebUrl: null,
                    RuntimeUrl: new Uri("http://127.0.0.1:5275/"),
                    RequestTimeout: CliOptions.DefaultRequestTimeout);
            });

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(CliConfigurationRequirements.Runtime, requirements);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Package_source_adoption_is_delegated_with_explicit_policy_and_safety_mode(bool dryRun)
    {
        RuntimeRegistrySourceAdoptionRequest? captured = null;
        var runtime = new FakeRuntimeClient
        {
            AdoptRegistrySource = (request, _) =>
            {
                captured = request;
                return Task.FromResult(Success(dryRun ? "Previewed." : "Adopted."));
            },
        };
        var mode = dryRun ? "--dry-run" : "--yes";

        var result = await CliTestHost.RunAsync([
            "package", "source", "adopt", "demo",
            "--registry-origin", "https://other.example/",
            "--tag", "beta",
            "--include-prerelease",
            "--allow-downgrade",
            mode,
        ], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("demo", captured?.PackageId);
        Assert.Equal("https://other.example/", captured?.RegistryOrigin);
        Assert.Equal("beta", captured?.Tag);
        Assert.Null(captured?.Version);
        Assert.True(captured?.IncludePrerelease);
        Assert.True(captured?.AllowDowngrade);
        Assert.Equal(dryRun, captured?.DryRun);
        Assert.Equal(!dryRun, captured?.Confirm);
    }

    [Theory]
    [InlineData(RuntimeRegistryAuthSessionState.Pending, RuntimeRegistryErrorCode.None, CliExitCodes.Timeout)]
    [InlineData(RuntimeRegistryAuthSessionState.Expired, RuntimeRegistryErrorCode.None, CliExitCodes.Timeout)]
    [InlineData(RuntimeRegistryAuthSessionState.Failed, RuntimeRegistryErrorCode.AuthenticationRequired, CliExitCodes.Authentication)]
    [InlineData(RuntimeRegistryAuthSessionState.Failed, RuntimeRegistryErrorCode.NotFound, CliExitCodes.NotFound)]
    [InlineData(RuntimeRegistryAuthSessionState.Failed, RuntimeRegistryErrorCode.RegistryUnavailable, CliExitCodes.Unavailable)]
    public async Task Auth_login_terminal_state_has_stable_exit_semantics(
        RuntimeRegistryAuthSessionState state,
        RuntimeRegistryErrorCode errorCode,
        int expectedExitCode)
    {
        var runtime = new FakeRuntimeClient
        {
            AuthStart = (_, _) => Task.FromResult(new RuntimeRegistryAuthStartResponse(
                "session-1", "https://registry.test/", "https://registry.test/login", DateTimeOffset.UtcNow.AddSeconds(-1))),
            AuthSession = (_, _) => Task.FromResult<RuntimeRegistryAuthSessionStatus?>(new(
                "session-1", "https://registry.test/", state, null, null, errorCode, "Sign-in did not complete.")),
        };

        var result = await CliTestHost.RunAsync(["registry", "auth", "login"], runtime);

        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    [Fact]
    public async Task Auth_status_propagates_typed_registry_unavailable_result()
    {
        var runtime = new FakeRuntimeClient
        {
            AuthStatus = (_, _) => Task.FromResult(new RuntimeRegistryAuthStatus(
                "https://registry.test/", false, null, null, RuntimeRegistryErrorCode.RegistryUnavailable, "Registry unavailable.")),
        };

        var result = await CliTestHost.RunAsync(["registry", "auth", "status"], runtime);

        Assert.Equal(CliExitCodes.Unavailable, result.ExitCode);
        Assert.Equal("Registry unavailable.\n", result.Error);
    }

    [Fact]
    public async Task Package_search_creates_only_the_registry_client()
    {
        var result = await CliTestHost.RunAsync(
            ["package", "search"],
            registry: new FakeRegistryClient(),
            runtimeFactory: _ => throw new InvalidOperationException("Runtime client must not be created."));

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
    }

    [Fact]
    public async Task Package_list_creates_only_the_runtime_client()
    {
        var result = await CliTestHost.RunAsync(
            ["package", "list"],
            runtime: new FakeRuntimeClient(),
            registryFactory: _ => throw new InvalidOperationException("Registry client must not be created."));

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
    }

    [Fact]
    public async Task Registry_yank_is_delegated_through_runtime_authentication()
    {
        RuntimeRegistryYankRequest? captured = null;
        var runtime = new FakeRuntimeClient
        {
            Yank = (request, _) =>
            {
                captured = request;
                return Task.FromResult(new RegistryPackageManagementOperationResponse(true, "Yanked.", []));
            },
        };

        var result = await CliTestHost.RunAsync(
            ["registry", "package", "yank", "demo", "1.2.3"],
            runtime,
            registryFactory: _ => throw new InvalidOperationException("Direct Registry client must not be created."));

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("https://registry.test/", captured?.RegistryOrigin);
        Assert.Equal("demo", captured?.PackageId);
        Assert.Equal("1.2.3", captured?.Version);
        Assert.True(captured?.IsYanked);
    }

    [Fact]
    public async Task Registry_stack_delete_is_delegated_through_runtime_authentication()
    {
        RuntimeRegistryDeleteStackRequest? captured = null;
        var runtime = new FakeRuntimeClient
        {
            DeleteStack = (request, _) =>
            {
                captured = request;
                return Task.FromResult(new RegistryStackManagementOperationResponse(true, "Deleted.", []));
            },
        };

        var result = await CliTestHost.RunAsync(["registry", "stack", "delete", "demo"], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("demo", captured?.StackId);
    }

    [Fact]
    public async Task Registry_tag_list_uses_only_the_anonymous_registry_client()
    {
        var registry = new FakeRegistryClient
        {
            DistTags = (packageId, _) => Task.FromResult<RegistryPackageDistTagsResponse?>(new(
                packageId,
                [new RegistryPackageDistTag("latest", "1.0.0", DateTimeOffset.UnixEpoch)])),
        };

        var result = await CliTestHost.RunAsync(
            ["registry", "package", "tag", "list", "demo"],
            registry: registry,
            runtimeFactory: _ => throw new InvalidOperationException("Runtime client must not be created."));

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("latest", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("enable", true)]
    [InlineData("disable", false)]
    public async Task Package_enabled_state_is_delegated_to_runtime(string commandName, bool expectedEnabled)
    {
        string? packageId = null;
        bool? enabled = null;
        var runtime = new FakeRuntimeClient
        {
            SetEnabled = (id, value, _) =>
            {
                packageId = id;
                enabled = value;
                return Task.FromResult(new PackageOperationResult(true, "Applied.", true, false, [], []));
            },
        };

        var result = await CliTestHost.RunAsync(["package", commandName, "demo"], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("demo", packageId);
        Assert.Equal(expectedEnabled, enabled);
    }

    [Fact]
    public async Task Package_uninstall_dry_run_returns_plan_without_mutation()
    {
        var uninstallCalled = false;
        var runtime = new FakeRuntimeClient
        {
            UninstallPlan = (_, _) => Task.FromResult(UninstallPlan(withCascade: true)),
            Uninstall = (_, _, _) =>
            {
                uninstallCalled = true;
                throw new InvalidOperationException("Dry run must not uninstall.");
            },
        };

        var result = await CliTestHost.RunAsync(
            ["package", "uninstall", "demo", "--dry-run", "--json"],
            runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.False(uninstallCalled);
        Assert.Contains("dependent", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"dryRun\":true", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Package_uninstall_requires_explicit_cascade_consent()
    {
        var runtime = new FakeRuntimeClient
        {
            UninstallPlan = (_, _) => Task.FromResult(UninstallPlan(withCascade: true)),
        };

        var result = await CliTestHost.RunAsync(
            ["package", "uninstall", "demo", "--yes"],
            runtime);

        Assert.Equal(CliExitCodes.Conflict, result.ExitCode);
        Assert.Contains("--cascade", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Package_uninstall_applies_exact_runtime_plan_token()
    {
        PackageUninstallRequest? captured = null;
        var runtime = new FakeRuntimeClient
        {
            UninstallPlan = (_, _) => Task.FromResult(UninstallPlan(withCascade: true)),
            Uninstall = (_, request, _) =>
            {
                captured = request;
                return Task.FromResult(new PackageOperationResult(true, "Uninstalled.", true, false, [], [])
                {
                    ChangeSet = new PackageLifecycleChangeSet([], [], [], ["demo", "dependent"], false),
                });
            },
        };

        var result = await CliTestHost.RunAsync(
            ["package", "uninstall", "demo", "--yes", "--cascade"],
            runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.True(captured?.AllowCascade);
        Assert.Equal("confirmation-token", captured?.ConfirmationToken);
    }

    private static PackageUninstallPlan UninstallPlan(bool withCascade)
        => new(
            "demo",
            [new PackageUninstallPlanPackage("demo", "Demo", "1.0.0")],
            withCascade ? [new PackageUninstallPlanPackage("dependent", "Dependent", "1.0.0")] : [],
            withCascade ? ["demo", "dependent"] : ["demo"],
            new PackageLifecycleChangeSet([], [], [], withCascade ? ["demo", "dependent"] : ["demo"], false),
            PackageUninstallDataBehavior.Retain,
            withCascade ? ["demo", "dependent"] : ["demo"],
            "confirmation-token");

    private static RuntimeRegistryPackageChangeResult Success(string message)
        => new(true, RuntimeRegistryErrorCode.None, message, true, false, [], [], ["demo"], []);

    private static PackageOperationResult PackageSuccess(string message)
        => new(true, message, true, false, [], []);

    private static InstalledPackageDescriptor InstalledPackage(
        string packageId,
        IReadOnlyList<string> capabilities)
        => new(
            packageId,
            packageId,
            "1.0.0",
            PackageHostRoles.Runtime,
            Summary: null,
            Icon: null,
            IsEnabled: true,
            DependsOn: [],
            DateTimeOffset.UnixEpoch,
            StatusMessage: null,
            GrantedSensitiveCapabilities: capabilities);

    private static SensitivePackageArchive CreateSensitivePackageArchive()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sunder-cli-capability-tests",
            Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var manifestDirectory = Path.Combine(source, "manifest");
        var rid = RuntimeInformation.RuntimeIdentifier;
        var executableName = rid.StartsWith("win-", StringComparison.Ordinal) ? "worker.exe" : "worker";
        var payloadDirectory = Path.Combine(source, "payload", "runtime", rid, "lib");
        Directory.CreateDirectory(manifestDirectory);
        Directory.CreateDirectory(payloadDirectory);
        File.WriteAllBytes(Path.Combine(payloadDirectory, executableName), [0x53, 0x55, 0x4e, 0x44, 0x45, 0x52]);
        var manifest = new SunderPackageManifest
        {
            ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
            ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
            Id = "demo.sensitive",
            Name = "Sensitive Demo",
            Version = "2.0.0",
            DependsOn = [],
            ContractBundles = [],
            UsesContracts = [],
            Provides = [],
            Targets =
            [
                new SunderPackageTargetManifest
                {
                    Role = SunderPackageFormat.RuntimeHostRole,
                    Rid = rid,
                    Kind = SunderPackageFormat.WorkerTargetKind,
                    EntryPoint = $"lib/{executableName}",
                    TargetFramework = "net10.0",
                    SdkVersion = "1.1.0",
                    RequiredHostCapabilities =
                    [
                        "sdk-baseline-1-1.v1",
                        "core.v1",
                        SunderSdkCapabilities.DockerEnvironmentV1,
                        "rpc.v2",
                        "worker-protocol.v3",
                    ],
                },
            ],
        };
        var manifestPath = Path.Combine(
            source,
            SunderPackageFormat.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        var manifestSha256 = HashFile(manifestPath);
        var indexPath = Path.Combine(
            source,
            SunderPackageFormat.ContentIndexPath.Replace('/', Path.DirectorySeparatorChar));
        var entries = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, indexPath, StringComparison.Ordinal))
            .Select(path => new FileInfo(path))
            .Select(file => new SunderPackageContentIndexEntry(
                Path.GetRelativePath(source, file.FullName).Replace('\\', '/'),
                HashFile(file.FullName),
                file.Length))
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        File.WriteAllText(
            indexPath,
            JsonSerializer.Serialize(new SunderPackageContentIndex(
                SunderPackageFormat.CurrentContentIndexVersion,
                entries)));
        var archivePath = Path.Combine(root, "demo.sensitive.sunderpkg");
        ZipFile.CreateFromDirectory(source, archivePath);
        return new SensitivePackageArchive(root, archivePath, manifestSha256);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record SensitivePackageArchive(
        string RootPath,
        string ArchivePath,
        string ManifestSha256);
}
