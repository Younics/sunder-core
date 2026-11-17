using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppWebViewProfileTests
{
    [Fact]
    public void WindowsProfilesBoundPackageNamesWithoutSharingPackageOrActivationOwnership()
    {
        var root = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "sunder-profile");
        var packageId = "test." + new string('a', 200);
        var activation = Guid.NewGuid();
        var path = AppWebViewProfile.ForPackage(root, packageId, activation, windows: true);
        Assert.Equal(Path.Combine(root, "webview-data"), Path.GetDirectoryName(path));
        Assert.Equal(43, Path.GetFileName(path).Length);
        Assert.Equal(path, AppWebViewProfile.ForPackage(root, packageId, activation, windows: true));
        Assert.NotEqual(path, AppWebViewProfile.ForPackage(root, packageId + "b", activation, windows: true));
        Assert.NotEqual(path, AppWebViewProfile.ForPackage(root, packageId, Guid.NewGuid(), windows: true));
        Assert.True(path.Length <= AppWebViewProfile.WindowsMaximumUserDataPathLength);
    }

    [Fact]
    public void WindowsProfileBudgetRejectsNativeDescendantOverflowWithoutCreatingOrRelocatingData()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var leafLength = Path.Combine("webview-data", new string('a', 43)).Length + 1;
        var rootLength = AppWebViewProfile.WindowsMaximumUserDataPathLength - leafLength;
        var exactRoot = root + new string('a', rootLength - root.Length);
        var activation = Guid.NewGuid();
        var exact = AppWebViewProfile.ForPackage(exactRoot, "test.package", activation, windows: true);
        Assert.Equal(AppWebViewProfile.WindowsMaximumUserDataPathLength, exact.Length);
        var error = Assert.Throws<PathTooLongException>(() =>
            AppWebViewProfile.ForPackage(exactRoot + "b", "test.package", activation, windows: true));
        Assert.Contains(AppLocalState.StateRootEnvironmentVariable, error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(exactRoot));
        Assert.Throws<PathTooLongException>(() => AppWebViewProfile.ValidateWindowsPath(new string('a', 184)));
    }

    [Fact]
    public void SharedEngineReservesTheIndependentProfileNameWithinTheOriginalDescendantBudget()
    {
        var maximum = AppWebViewProfile.WindowsMaximumSharedUserDataPathLength;
        Assert.Equal(147, maximum);
        Assert.Equal(AppWebViewProfile.WindowsMaximumUserDataPathLength, maximum + "view-".Length + 32 - "sunder-package".Length);
        AppWebViewProfile.ValidateWindowsPath(new string('a', maximum), maximum);
        var error = Assert.Throws<PathTooLongException>(() => AppWebViewProfile.ValidateWindowsPath(new string('a', maximum + 1), maximum));
        Assert.Contains(AppLocalState.StateRootEnvironmentVariable, error.Message, StringComparison.Ordinal);
        // The reproduced 161-character shared root fit the old UDF-only guard
        // but stalled native exit. Reject it before acquiring WebView2.
        Assert.Throws<PathTooLongException>(() => AppWebViewProfile.ValidateWindowsPath(new string('a', 161), maximum));
    }
}
