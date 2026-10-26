using Sunder.App.Services;
using Sunder.App.ViewModels;
using Xunit;

namespace Sunder.App.Tests;

public sealed class SettingsRuntimeResourceFieldsTests
{
    [Fact]
    public async Task PresetsDisplayTheirOwnReadOnlyValuesWithoutOverwritingCustomDraft()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-resource-fields", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new AppRuntimeResourcePolicySettingsService(Path.Combine(root, "policy.json"));
            var vm = new SettingsRuntimeResourcesViewModel(new SettingsRuntimeResourcesCoordinator(service,
                new UnavailableRuntimeResourcePolicyStatusSource()));
            vm.LoadSettings();
            Assert.True(vm.AreLimitsReadOnly);
            Assert.Equal("32", vm.CustomRpcValueMiBText);
            vm.CustomRpcValueMiBText = "7";
            Assert.Equal("32", vm.CustomRpcValueMiBText);
            Select(RuntimeResourcePolicyPreset.Custom);
            vm.CustomRpcValueMiBText = "12";
            vm.CustomObjectMembersText = "unfinished";
            Assert.True(vm.HasValidationError);
            Select(RuntimeResourcePolicyPreset.Large);
            Assert.True(vm.AreLimitsReadOnly);
            Assert.Equal("64", vm.CustomRpcValueMiBText);
            Assert.Equal("131072", vm.CustomObjectMembersText);
            Assert.False(vm.HasValidationError);
            Assert.True((await vm.SaveSettingsAsync(CancellationToken.None)).Success);
            Assert.Equal(RuntimeResourcePolicyPreset.Large, service.Load().Preset);
            Select(RuntimeResourcePolicyPreset.Custom);
            Assert.False(vm.AreLimitsReadOnly);
            Assert.Equal("12", vm.CustomRpcValueMiBText);
            Assert.Equal("unfinished", vm.CustomObjectMembersText);
            Assert.False((await vm.SaveSettingsAsync(CancellationToken.None)).Success);
            Assert.Equal(RuntimeResourcePolicyPreset.Large, service.Load().Preset);
            vm.ResetToDefaultsCommand.Execute(null);
            Select(RuntimeResourcePolicyPreset.Custom);
            Assert.Equal("32", vm.CustomRpcValueMiBText);
            Assert.Equal("65536", vm.CustomObjectMembersText);

            void Select(RuntimeResourcePolicyPreset preset)
                => vm.SelectedPresetOption = vm.PresetOptions.Single(option => option.Preset == preset);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
