using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Sunder.App.Tests.TestSupport;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class ReviewWindowLayoutTests
{
    [Fact]
    public async Task ArchiveValidationAndRuntimeImpactDetermineTheVisibleActions()
    {
        await HeadlessAppCollection.Session.Dispatch(async () =>
        {
            var owner = new Window();
            owner.Show();
            try
            {
                var file = new string('x', 85) + ".sunderpkg";
                var digest = new string('c', 64);
                var archive = LocalPackageInstallConsentViewModel.Valid(file, LocalPackageInstallConsentViewModelTests.SampleArchive(), digest, 2048);
                await CheckReview(new LocalPackageInstallConsentWindow(archive), "Approve & Install", "valid");
                await CheckReview(new LocalPackageInstallConsentWindow(LocalPackageInstallConsentViewModel.Invalid(file, ["Manifest digest mismatch."])), null, "invalid");
                await CheckReview(new RuntimeConfirmationWindow(RuntimeConfirmationViewModel.Install(RuntimeManagementTestClient.InstallPlan())), "Download & Install Runtime", "install");
                var dependent = "sample." + new string('d', 90);
                var removal = RuntimeConfirmationViewModel.Uninstall(new PackageUninstallPlan("sample", [new("sample", "Sample", "1.0.0")],
                    [new(dependent, dependent, "1.0.0")], ["sample", dependent], new([], [], [], [], false),
                    PackageUninstallDataBehavior.Retain, ["sample", dependent], digest));
                await CheckReview(new RuntimeConfirmationWindow(removal), "Uninstall 2 packages", "removal");
                var binding = new PackageTargetExecutionRuntimeBinding("runtime", RuntimeManagementTestClient.Identity.Rid,
                    RuntimeManagementTestClient.Requirement, RuntimeManagementTestClient.Identity, RuntimeManagementTestClient.InstalledArtifact(), DateTimeOffset.UtcNow);
                var repair = new ExecutionRuntimeRepairConfirmationViewModel(PackageExecutionRuntimeStatusViewModel.Create(binding, null),
                    new(2, ["sample", dependent], digest));
                await CheckReview(new ExecutionRuntimeRepairConfirmationWindow(repair), "Repair and Reload");
            }
            finally { owner.Close(); }
            return true;

            async Task CheckReview(Window dialog, string? approval, string? state = null)
            {
                var result = dialog.ShowDialog<bool>(owner);
                try
                {
                    await ManagementWindowAssert.CheckLayoutsAsync(dialog, () =>
                    {
                        var buttons = dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("primary-action"));
                        if (approval is null) Assert.Empty(buttons);
                        else
                        {
                            var button = Assert.Single(buttons);
                            Assert.Equal(approval, button.Content);
                            if (dialog.DataContext is RuntimeConfirmationViewModel { IsRemoval: true })
                                Assert.Same(Application.Current!.Resources["Sunder.Brush.Danger.Soft"], button.Background);
                        }
                    }, state);
                    var dismiss = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, approval is null ? "Close" : "Cancel"));
                    ManagementWindowAssert.Click(dialog, dismiss);
                    Assert.False(await result);
                }
                finally { if (dialog.IsVisible) dialog.Close(); }
            }
        }, default);
    }
}
