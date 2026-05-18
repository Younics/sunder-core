using Sunder.App.Features.Shell.Menus;
using Sunder.App.Models;
using Sunder.App.ViewModels;
using Sunder.Protocol;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackageViewMenuProjectorTests
{
    [Fact]
    public void Project_GroupsViewsByPackageAndPreservesHotbarState()
    {
        var packageIcon = new PackageIconDescriptor(null, "assets/package.svg");
        var chatIcon = new PackageIconDescriptor(null, "assets/chat.svg");
        var sessionsIcon = new PackageIconDescriptor(null, "assets/sessions.svg");
        var projected = PackageViewMenuProjector.Project(
            [
                CreateView("agent.sessions", "agent", "Sunder Agent", "Sessions", "S", RailPlacement.RightTop, sessionsIcon, packageIcon),
                CreateView("tools.search", "tools", "Sunder Tools", "Search", "T", RailPlacement.LeftTop),
                CreateView("agent.chat", "agent", "Sunder Agent", "Chat", "A", RailPlacement.Middle, chatIcon, packageIcon),
            ],
            CreateIconUri,
            viewId => string.Equals(viewId, "agent.chat", StringComparison.OrdinalIgnoreCase));

        Assert.Collection(
            projected,
            agentGroup =>
            {
                Assert.Equal("agent", agentGroup.PackageId);
                Assert.Equal("Sunder Agent", agentGroup.PackageDisplayName);
                Assert.Equal(new Uri("file:///agent/assets/package.svg"), agentGroup.PackageIconUri);
                Assert.Collection(
                    agentGroup.Views,
                    view =>
                    {
                        Assert.Equal("agent.chat", view.ViewId);
                        Assert.Equal("Chat", view.Title);
                        Assert.Equal(new Uri("file:///agent/assets/chat.svg"), view.IconUri);
                        Assert.True(view.IsInHotbar);
                    },
                    view =>
                    {
                        Assert.Equal("agent.sessions", view.ViewId);
                        Assert.Equal("Sessions", view.Title);
                        Assert.Equal(new Uri("file:///agent/assets/sessions.svg"), view.IconUri);
                        Assert.False(view.IsInHotbar);
                    });
            },
            toolsGroup =>
            {
                Assert.Equal("tools", toolsGroup.PackageId);
                Assert.Equal("Sunder Tools", toolsGroup.PackageDisplayName);
                Assert.Null(toolsGroup.PackageIconUri);
                Assert.Collection(toolsGroup.Views, view =>
                {
                    Assert.Equal("tools.search", view.ViewId);
                    Assert.False(view.IsInHotbar);
                });
            });
    }

    private static ShellPackageView CreateView(
        string viewId,
        string packageId,
        string packageDisplayName,
        string title,
        string glyph,
        RailPlacement placement,
        PackageIconDescriptor? icon = null,
        PackageIconDescriptor? packageIcon = null)
        => new(
            viewId,
            packageId,
            packageDisplayName,
            "1.0.0",
            title,
            glyph,
            placement,
            PackageReadinessState.Ready,
            ShowInHotbarByDefault: false,
            icon,
            PackageGlyph: packageDisplayName[0].ToString(),
            packageIcon);

    private static Uri? CreateIconUri(string packageId, PackageIconDescriptor? icon)
        => icon?.AssetPath is null ? null : new Uri($"file:///{packageId}/{icon.AssetPath}");
}
