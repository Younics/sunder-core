using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Runtime.Contracts;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed partial class StacksWindowViewModelTests
{
    [Fact]
    public async Task UseStackWizard_ManifestInputsRemainContributorScopedAndFollowFragmentSelection()
    {
        var root = CreateTempDirectory();
        var path = await CreateStackArchiveAsync(root, "inputs-stack", "Inputs Stack", includeFragment: true, includeRequiredInput: true, fragmentDefaultSelected: true);
        var library = new LocalStackLibraryService(Path.Combine(root, "library"));
        var manifest = await library.ReadManifestAsync(path);
        var second = new SunderStackFragmentManifest
        {
            FragmentId = "second", OwnerPackageId = "younics.ai.agent", ContributorId = "other", SchemaId = "other/profile", SchemaVersion = 1,
            DisplayName = "Other", DefaultSelected = true, PayloadPath = "payload/fragments/agent-profile.json",
            RequiredInputs = [new() { InputId = "api-key", Label = "Other key", Sensitivity = "Secret", Required = true }],
        };
        await SunderStackArchiveWriter.WriteAsync(new SunderStackManifest
        {
            SchemaVersion = 1, MinReaderVersion = 1, StackId = manifest.StackId, Name = manifest.Name,
            Packages = manifest.Packages, Fragments = [.. manifest.Fragments!, second],
        }, path, new Dictionary<string, string> { ["payload/fragments/agent-profile.json"] = Path.Combine(root, "agent-profile.json") });
        var stack = await library.ImportAsync(path);
        using var runtime = new FakeRuntimeApiClient
        {
            RegistryPlan = new(true, [new("younics.ai.agent", null, "1.0.0", "identity", RegistryPackageActionKinds.PackageInstall, null, [], [], [])], [], [], [], []),
        };
        await using var viewModel = new UseStackWizardViewModel(library, stack, runtime,
            new RegistryPackageInstallService((_, _) => Task.FromResult(true)), (_, _) => Task.CompletedTask,
            _ => new FakeRegistryApiClient(), "https://registry.example/");
        await viewModel.InitializeAsync();
        Assert.Equal(2, viewModel.RequiredInputs.Count);
        Assert.Equal(2, viewModel.RequiredInputs.Select(input => input.ContributorId).Distinct().Count());
        viewModel.RequiredInputs.Single(input => input.ContributorId == "agent").Value = "kept-value";
        viewModel.SetupPackageGroups.Single().Items.Single(item => item.ContributorId == "other").IsSelected = false;
        Assert.Equal("kept-value", Assert.Single(viewModel.RequiredInputs).Value);
        Assert.Equal(0, runtime.ImportPreviewCount);
    }

    [Fact]
    public async Task UseStackWizard_ChangedPackageFingerprintRequiresReviewBeforeAnyInstallation()
    {
        var root = CreateTempDirectory();
        var path = await CreateStackArchiveAsync(root, "package-stack", "Package Stack");
        var library = new LocalStackLibraryService(Path.Combine(root, "library"));
        var stack = await library.ImportAsync(path);
        using var runtime = new FakeRuntimeApiClient
        {
            RegistryPlan = new(true, [new("younics.ai.agent", null, "1.0.0", "identity", RegistryPackageActionKinds.PackageInstall, null, [], [], [])], [], [], [], [], ResolutionFingerprint: new string('f', 64)),
        };
        var confirmations = 0;
        await using var viewModel = new UseStackWizardViewModel(library, stack, runtime,
            new RegistryPackageInstallService((_, _) => { confirmations++; return Task.FromResult(true); }), (_, _) => Task.CompletedTask,
            _ => new FakeRegistryApiClient(), "https://registry.example/");
        await viewModel.InitializeAsync();
        runtime.RegistryPlan = runtime.RegistryPlan with { ResolutionFingerprint = new string('a', 64) };
        await viewModel.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(0, confirmations);
        Assert.Null(runtime.LastRegistryApplyRequest);
        Assert.Contains("plan changed", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UseStackWizard_ChangedPreviewActionsRequireAnotherReviewBeforeApply()
    {
        var root = CreateTempDirectory();
        var path = await CreateStackArchiveAsync(root, "review-stack", "Review Stack", includeFragment: true, includePackage: false, fragmentDefaultSelected: true);
        var library = new LocalStackLibraryService(Path.Combine(root, "library"));
        var stack = await library.ImportAsync(path);
        var calls = 0;
        using var runtime = new FakeRuntimeApiClient
        {
            PreviewStackImportFactory = (_, _) => Task.FromResult(new RuntimeStackImportPreviewResponse(
                true, "plan-" + ++calls, DateTimeOffset.UtcNow.AddMinutes(15),
                [new("action", "younics.ai.agent", "agent", "local-action", calls == 1 ? "Create profile" : "Replace profile", calls == 1 ? "Create" : "Replace", true)],
                [], [], [], [])),
        };
        await using var viewModel = new UseStackWizardViewModel(library, stack, runtime,
            new RegistryPackageInstallService((_, _) => Task.FromResult(true)), (_, _) => Task.CompletedTask,
            _ => new FakeRegistryApiClient(), "https://registry.example/");
        await viewModel.InitializeAsync();
        await viewModel.ApplyCommand.ExecuteAsync(null);
        Assert.Null(runtime.LastImportRequest);
        Assert.Contains("Review the refreshed", viewModel.StatusText, StringComparison.Ordinal);
        await viewModel.ApplyCommand.ExecuteAsync(null);
        Assert.NotNull(runtime.LastImportRequest);
        Assert.Equal(["action"], runtime.LastImportRequest.SelectedActionIds);
    }
}
