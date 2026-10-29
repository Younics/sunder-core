using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Sunder.App.Features.Shell.Menus;
using Sunder.App.Views;
using Xunit;

namespace Sunder.App.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class MacNativeMenuControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TopLevelUsesTextOnlyWhileNestedItemsKeepTheirIcons(bool hasRootIcon)
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(MainWindowViewModelShellViewTests.SplitterTestApplication));
        await ui.Dispatch(() =>
        {
            var window = new Window();
            using var icon = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96));
            using var controller = new MacNativeMenuController(window, () => null);
            var leaf = new ShellMenuItem("view:chat", "Chat", null, icon, false, []);
            var child = new ShellMenuItem("package:agent", "AI Agent", null, icon, true, [leaf]);
            var root = new ShellMenuItem("view", "View", null, hasRootIcon ? icon : null, true, [child]);

            var nativeRoot = controller.BuildNativeItem(root, isTopLevel: true);

            Assert.Equal("View", nativeRoot.Header);
            Assert.Null(nativeRoot.Icon);
            var nativeChild = Assert.IsType<NativeMenuItem>(Assert.Single(nativeRoot.Menu!.Items));
            Assert.Equal("AI Agent", nativeChild.Header);
            Assert.Same(icon, nativeChild.Icon);
            var nativeLeaf = Assert.IsType<NativeMenuItem>(Assert.Single(nativeChild.Menu!.Items));
            Assert.Equal("Chat", nativeLeaf.Header);
            Assert.Same(icon, nativeLeaf.Icon);
            Assert.False(nativeLeaf.IsEnabled);
            window.Close();
        }, default);
    }
}
