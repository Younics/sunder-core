using System.Text;
using System.Text.Json;
using Sunder.App.Services;
using static Sunder.App.Tests.TestSupport.TestPaths;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppRuntimeResourcePolicySettingsServiceTests
{
    [Fact]
    public void DefaultPathRemainsInAppV1State()
    {
        var path = AppRuntimeResourcePolicySettingsService.GetDefaultSettingsFilePath();

        Assert.EndsWith(
            Path.Combine("Sunder", "app", "v1", "runtime-resource-policy.json"),
            path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    [Fact]
    public void SaveAtomicallyRoundTripsPresetAndPreservesCustomValues()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "runtime-resource-policy.json");
        var custom = new RuntimeResourcePolicy(
            12 * 1024 * 1024,
            48,
            1_000_000,
            10_000,
            768 * 1024,
            100 * 1024 * 1024);
        var service = new AppRuntimeResourcePolicySettingsService(path);

        service.Save(new RuntimeResourcePolicySelection(RuntimeResourcePolicyPreset.Standard, custom));
        var reloaded = new AppRuntimeResourcePolicySettingsService(path).Load();

        Assert.Equal(RuntimeResourcePolicyPreset.Standard, reloaded.Preset);
        Assert.Equal(custom, reloaded.CustomPolicy);
        Assert.Empty(Directory.EnumerateFiles(root, ".runtime-resource-policy.json.*.tmp"));
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public void Load_InvalidCustomValueIsQuarantinedAndFallsBackToStandardWithoutClamping()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "runtime-resource-policy.json");
        var json = Encoding.UTF8.GetString(RuntimeResourcePolicyContract.SerializeSettings(
            RuntimeResourcePolicies.DefaultSelection,
            writeIndented: false));
        File.WriteAllText(path, json.Replace(
            "\"contentReferenceBytes\":67108864",
            "\"contentReferenceBytes\":268435457",
            StringComparison.Ordinal));
        var service = new AppRuntimeResourcePolicySettingsService(path);

        var loaded = service.Load();

        Assert.Equal(RuntimeResourcePolicyPreset.Standard, loaded.Preset);
        Assert.Equal(RuntimeResourcePolicies.Standard, loaded.EffectivePolicy);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.EnumerateFiles(root, "runtime-resource-policy.json.corrupt.*"));
    }

    [Fact]
    public void Load_CorruptJsonIsQuarantinedAndReturnsStandard()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "runtime-resource-policy.json");
        File.WriteAllText(path, "not-json");

        var loaded = new AppRuntimeResourcePolicySettingsService(path).Load();

        Assert.Equal(RuntimeResourcePolicies.DefaultSelection, loaded);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.EnumerateFiles(root, "runtime-resource-policy.json.corrupt.*"));
    }
}
