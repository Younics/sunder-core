using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli.Tests;

public sealed class RuntimeStackCommandTests
{
    [Fact]
    public async Task Stack_import_dry_run_uses_runtime_preview_defaults_and_discards_the_plan()
    {
        var stackPath = await CreatePackageOnlyStackAsync();
        string? discardedPlan = null;
        RuntimeStackImportPreviewRequest? captured = null;
        try
        {
            var runtime = new FakeRuntimeClient
            {
                StackUpload = (path, _) =>
                {
                    Assert.Equal(stackPath, path);
                    return Task.FromResult(new ContentUploadDescriptor("upload-1", new string('a', 64), 1, "demo.sunderstack", "application/vnd.sunder.stack"));
                },
                StackPreview = (request, _) =>
                {
                    captured = request;
                    return Task.FromResult(new RuntimeStackImportPreviewResponse(
                        true,
                        "plan-1",
                        DateTimeOffset.UtcNow.AddMinutes(1),
                        [new RuntimeStackImportActionDescriptor("action-1", "demo", "setup", "local-1", "Create profile", "profile", true)],
                        [new RuntimeStackRequiredInputDescriptor("demo/setup/input", "demo", "setup", "input", "API key", RuntimeStackInputSensitivity.Secret, true)],
                        [],
                        [],
                        []));
                },
                StackDiscard = (planId, _) =>
                {
                    discardedPlan = planId;
                    return Task.CompletedTask;
                },
            };

            var result = await CliTestHost.RunAsync(
                ["stack", "import", "--file", stackPath, "--dry-run", "--json"],
                runtime);

            Assert.Equal(CliExitCodes.Success, result.ExitCode);
            Assert.Empty(captured?.SelectedFragmentIds ?? ["unexpected"]);
            Assert.Empty(captured?.InputValues ?? new Dictionary<string, string> { ["unexpected"] = "value" });
            Assert.Equal("plan-1", discardedPlan);
            using var document = JsonDocument.Parse(result.Output);
            var data = document.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("dryRun").GetBoolean());
            Assert.Equal("secret", data.GetProperty("inputs")[0].GetProperty("sensitivity").GetString());
            Assert.False(data.GetProperty("inputs")[0].TryGetProperty("defaultValue", out _));
            Assert.Contains("input", string.Join(' ', data.GetProperty("blockers").EnumerateArray().Select(item => item.GetString())), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(stackPath);
        }
    }

    [Fact]
    public async Task Stack_export_selects_defaults_and_never_projects_discovered_values()
    {
        const string undisclosedValue = "raw-value-that-must-not-appear";
        RuntimeStackExportRequest? captured = null;
        string? downloadedTo = null;
        var outputPath = Path.Combine(Path.GetTempPath(), $"sunder-cli-stack-export-{Guid.NewGuid():N}.sunderstack");
        var runtime = new FakeRuntimeClient
        {
            StackExportItems = _ => Task.FromResult(new RuntimeStackExportDiscoveryResponse(
                [new RuntimeStackExportItemDescriptor(
                    "setup",
                    "demo",
                    "profile",
                    "Profile",
                    "profile",
                    true,
                    Details: [new RuntimeStackExportItemDetail("API key", undisclosedValue, "Secret", DetailId: "api-key")])],
                [],
                [])),
            StackExport = (request, _) =>
            {
                captured = request;
                return Task.FromResult(new RuntimeStackExportResponse(
                    true,
                    new ContentDownloadDescriptor("download-1", new string('b', 64), 10, "demo.sunderstack", "application/vnd.sunder.stack", "downloads/download-1"),
                    [],
                    []));
            },
            ContentDownload = (_, path, _) =>
            {
                downloadedTo = path;
                return Task.CompletedTask;
            },
        };

        var result = await CliTestHost.RunAsync(
            ["stack", "export", "demo", "--output", outputPath, "--json"],
            runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(Path.GetFullPath(outputPath), downloadedTo);
        var selection = Assert.Single(captured!.SelectedItems);
        var detail = Assert.Single(selection.Details!);
        Assert.Equal("api-key", detail.DetailId);
        Assert.Null(detail.ValueOverride);
        Assert.DoesNotContain(undisclosedValue, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(undisclosedValue, result.Error, StringComparison.Ordinal);
    }

    private static async Task<string> CreatePackageOnlyStackAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sunder-cli-stack-import-{Guid.NewGuid():N}.sunderstack");
        await SunderStackArchiveWriter.WriteAsync(new SunderStackManifest
        {
            SchemaVersion = SunderStackFormat.CurrentSchemaVersion,
            MinReaderVersion = SunderStackFormat.CurrentReaderVersion,
            StackId = "demo",
            Name = "Demo",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            UpdatedAtUtc = DateTimeOffset.UnixEpoch,
            Packages = [new SunderStackPackageRequirement
            {
                PackageId = "demo",
                InstallTag = "latest",
                Required = true,
            }],
            Fragments = [],
        }, path);
        return path;
    }
}
