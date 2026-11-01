using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Client;
using System.Net;

namespace Sunder.Cli.Tests;

public sealed partial class RuntimeStackCommandTests
{
    [Fact]
    public async Task Stack_export_dry_run_lists_explicit_item_ids_without_exporting_or_disclosing_values()
    {
        var exports = 0;
        var runtime = new FakeRuntimeClient
        {
            StackExportItems = _ => Task.FromResult(new RuntimeStackExportDiscoveryResponse(
                [new("setup", "demo", "profile", "Profile", "example", false,
                    Details: [new("Setting", "undisclosed-discovered-value", "Public", DetailId: "setting")])], [], [])),
            StackExport = (_, _) => { exports++; throw new InvalidOperationException("Dry run must not export"); },
        };
        var result = await CliTestHost.RunAsync(["stack", "export", "demo", "--dry-run", "--item", "demo/setup/profile", "--json"], runtime);
        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(0, exports);
        Assert.Contains("demo/setup/profile", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("undisclosed-discovered-value", result.Output + result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stack_import_public_default_is_bound_by_another_preview_before_apply()
    {
        var path = await CreateFragmentStackAsync();
        var previews = 0;
        var runtime = new FakeRuntimeClient
        {
            StackUpload = (_, _) => Task.FromResult(new ContentUploadDescriptor("upload", new string('a', 64), 1, "demo.sunderstack", "application/vnd.sunder.stack")),
            StackPreview = (request, _) =>
            {
                if (++previews == 1) Assert.Empty(request.InputValues);
                else Assert.Equal("us-east", request.InputValues["region"]);
                return Task.FromResult(new RuntimeStackImportPreviewResponse(true, "plan-" + previews, DateTimeOffset.UtcNow.AddMinutes(15),
                    [new("action", "demo", "setup", "local", "Create item", "Create", true)],
                    [new("region", "demo", "setup", "region", "Region", RuntimeStackInputSensitivity.Public, true, DefaultValue: "us-east")], [], [], []));
            },
            StackImport = (request, _) =>
            {
                Assert.Equal("plan-2", request.PlanId);
                return Task.FromResult(new RuntimeStackImportResponse(RuntimeStackImportOutcome.Completed, [], new Dictionary<string, string>(), [], [], []));
            },
        };
        try
        {
            var result = await CliTestHost.RunAsync(["stack", "import", "--file", path, "--yes", "--json"], runtime);
            Assert.Equal(CliExitCodes.Success, result.ExitCode);
            Assert.Equal(2, previews);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Stack_import_without_a_registry_uses_only_active_local_packages()
    {
        var path = await CreatePackageOnlyStackAsync();
        var runtime = new FakeRuntimeClient
        {
            ResolveRegistry = (_, _) => throw new InvalidOperationException("No Registry must be contacted"),
            StackUpload = (_, _) => Task.FromResult(new ContentUploadDescriptor("upload", new string('a', 64), 1, "demo.sunderstack", "application/vnd.sunder.stack")),
            StackPreview = (_, _) => Task.FromResult(new RuntimeStackImportPreviewResponse(true, "plan", DateTimeOffset.UtcNow.AddMinutes(15), [], [], [], [], [])),
        };
        try
        {
            var result = await CliTestHost.RunAsync(["stack", "import", "--file", path, "--yes", "--no-fragments", "--json"], runtime,
                optionsFactory: (_, _) => new CliOptions(null, null, new("http://127.0.0.1:5275/"), TimeSpan.FromMinutes(15)));
            Assert.Equal(CliExitCodes.Success, result.ExitCode);
            Assert.Contains("No Registry is configured", result.Output, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stack_import_applies_explicit_selections_and_environment_inputs_without_disclosure(bool json)
    {
        var path = await CreateFragmentStackAsync();
        const string value = "private-input-value\nprivate-second-line";
        var imports = 0;
        var runtime = new FakeRuntimeClient
        {
            StackUpload = (_, _) => Task.FromResult(new ContentUploadDescriptor("upload", new string('a', 64), 1, "demo.sunderstack", "application/vnd.sunder.stack")),
            StackPreview = (request, _) =>
            {
                Assert.False(request.UseDefaultFragments);
                Assert.Equal(["fragment"], request.SelectedFragmentIds);
                Assert.Equal(value, request.InputValues["scoped-input"]);
                return Task.FromResult(new RuntimeStackImportPreviewResponse(true, "plan", DateTimeOffset.UtcNow.AddMinutes(15),
                    [new("action", "demo", "setup", "local", value, "Create", false)],
                    [new("scoped-input", "demo", "setup", "input", "Input", RuntimeStackInputSensitivity.Secret, true)], [], [], []));
            },
            StackImport = (request, _) =>
            {
                imports++;
                Assert.Equal("plan", request.PlanId);
                Assert.Equal(["action"], request.SelectedActionIds);
                return Task.FromResult(new RuntimeStackImportResponse(RuntimeStackImportOutcome.Completed,
                    [new("item", "demo", "setup", value, "example")], new Dictionary<string, string>(), [], [], []));
            },
        };
        var reader = new FakePackageSecretValueReader
        {
            Read = (source, name, _) =>
            {
                Assert.Equal(PackageSecretSource.Environment, source);
                Assert.Equal("STACK_VALUE", name);
                return ValueTask.FromResult(new PackageSecretValue(value));
            },
        };
        try
        {
            string[] args = ["stack", "import", "--file", path, "--yes", "--fragment", "fragment", "--action", "action",
                "--input-env", "scoped-input=STACK_VALUE"];
            var result = await CliTestHost.RunAsync(json ? [.. args, "--json"] : args, runtime, packageSecrets: reader);
            Assert.Equal(CliExitCodes.Success, result.ExitCode);
            Assert.Equal(1, imports);
            Assert.DoesNotContain(value, result.Output + result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("private-second-line", result.Output + result.Error, StringComparison.Ordinal);
            Assert.Contains("Completed", result.Output, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stack_import_transport_failure_keeps_supplied_values_redacted_after_handler_unwinds(bool json)
    {
        var path = await CreateFragmentStackAsync();
        const string value = "private-input-value";
        var runtime = new FakeRuntimeClient
        {
            StackUpload = (_, _) => throw new RuntimeClientException(HttpStatusCode.BadRequest,
                "Rejected " + value, detail: value, errorCode: "runtime." + value,
                correlationId: value, innerException: null),
        };
        var reader = new FakePackageSecretValueReader
        {
            Read = (_, _, _) => ValueTask.FromResult(new PackageSecretValue(value)),
        };
        try
        {
            string[] args = ["stack", "import", "--file", path, "--yes", "--input-env", "scoped-input=STACK_VALUE"];
            var result = await CliTestHost.RunAsync(json ? [.. args, "--json"] : args, runtime, packageSecrets: reader);
            Assert.Equal(CliExitCodes.Failure, result.ExitCode);
            Assert.DoesNotContain(value, result.Output + result.Error, StringComparison.Ordinal);
            Assert.Contains("REDACTED", result.Output + result.Error, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Stack_import_missing_input_blocks_apply_but_remains_discoverable_in_dry_run()
    {
        var path = await CreateFragmentStackAsync();
        var imports = 0;
        var runtime = new FakeRuntimeClient
        {
            StackUpload = (_, _) => Task.FromResult(new ContentUploadDescriptor("upload", new string('a', 64), 1, "demo.sunderstack", "application/vnd.sunder.stack")),
            StackPreview = (_, _) => Task.FromResult(new RuntimeStackImportPreviewResponse(true, "plan", DateTimeOffset.UtcNow.AddMinutes(15),
                [new("action", "demo", "setup", "local", "Create item", "Create", true)],
                [new("scoped-input", "demo", "setup", "input", "Input", RuntimeStackInputSensitivity.Secret, true)], [], [], [])),
            StackImport = (_, _) => { imports++; throw new InvalidOperationException("Must not apply"); },
        };
        try
        {
            var preview = await CliTestHost.RunAsync(["stack", "import", "--file", path, "--dry-run", "--json"], runtime);
            Assert.Equal(CliExitCodes.Success, preview.ExitCode);
            Assert.Contains("scoped-input", preview.Output, StringComparison.Ordinal);
            var applied = await CliTestHost.RunAsync(["stack", "import", "--file", path, "--yes", "--json"], runtime);
            Assert.Equal(CliExitCodes.Conflict, applied.ExitCode);
            Assert.Equal(0, imports);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Stack_import_installs_packages_with_exact_preflight_before_setup_preview()
    {
        var path = await CreatePackageOnlyStackAsync();
        var events = new List<string>();
        var runtime = new FakeRuntimeClient
        {
            ResolveRegistry = (_, _) => Task.FromResult(new RuntimeRegistryResolveInstallPlanResponse(true,
                [new("demo", null, "1.0.0", "identity", RegistryPackageActionKinds.PackageInstall, null, [], [], [])], [], [], [], [], ResolutionFingerprint: new string('f', 64))),
            ApplyRegistryPlan = (request, _) =>
            {
                events.Add("packages");
                Assert.Equal(new string('f', 64), request.ExpectedResolutionFingerprint);
                return Task.FromResult(new RuntimeRegistryPackageChangeResult(true, RuntimeRegistryErrorCode.None, "Installed", true, false, [], [], ["demo"], []));
            },
            StackUpload = (_, _) => Task.FromResult(new ContentUploadDescriptor("upload", new string('a', 64), 1, "demo.sunderstack", "application/vnd.sunder.stack")),
            StackPreview = (_, _) =>
            {
                events.Add("preview");
                return Task.FromResult(new RuntimeStackImportPreviewResponse(true, "plan", DateTimeOffset.UtcNow.AddMinutes(15), [], [], [], [], []));
            },
        };
        try
        {
            var result = await CliTestHost.RunAsync(["stack", "import", "--file", path, "--yes", "--no-actions", "--json"], runtime);
            Assert.Equal(CliExitCodes.Success, result.ExitCode);
            Assert.Equal(["packages", "preview"], events);
            Assert.Contains("packagePreflight", result.Output, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{\"input\":\"do-not-print\",\"INPUT\":\"other\"}")]
    [InlineData("{\"input\":\"do-not-print\",broken}")]
    public async Task Stack_import_rejects_bad_stdin_without_uploading_or_printing_values(string json)
    {
        var path = await CreateFragmentStackAsync();
        var uploaded = false;
        var runtime = new FakeRuntimeClient { StackUpload = (_, _) => { uploaded = true; throw new InvalidOperationException(); } };
        var reader = new FakePackageSecretValueReader { Read = (_, _, _) => ValueTask.FromResult(new PackageSecretValue(json)) };
        try
        {
            var result = await CliTestHost.RunAsync(["stack", "import", "--file", path, "--yes", "--inputs-stdin", "--json"], runtime, packageSecrets: reader);
            Assert.NotEqual(CliExitCodes.Success, result.ExitCode);
            Assert.False(uploaded);
            Assert.DoesNotContain("do-not-print", result.Output + result.Error, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    private static async Task<string> CreateFragmentStackAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sunder-cli-stack-{Guid.NewGuid():N}.sunderstack");
        var payload = path + ".json";
        await File.WriteAllTextAsync(payload, "{}");
        try
        {
            await SunderStackArchiveWriter.WriteAsync(new SunderStackManifest
            {
                SchemaVersion = 1, MinReaderVersion = 1, StackId = "demo", Name = "Demo", Packages = [],
                Fragments = [new() { FragmentId = "fragment", OwnerPackageId = "demo", ContributorId = "setup", SchemaId = "demo/item", SchemaVersion = 1,
                    DisplayName = "Item", DefaultSelected = true, PayloadPath = "payload/fragments/item.json" }],
            }, path, new Dictionary<string, string> { ["payload/fragments/item.json"] = payload });
        }
        finally { File.Delete(payload); }
        return path;
    }
}
