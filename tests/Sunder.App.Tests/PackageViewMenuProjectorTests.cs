using Avalonia.Media;
using Sunder.App.Features.Shell.Menus;
using Sunder.App.Models;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackageViewMenuProjectorTests
{
    [Fact]
    public async Task Project_BuildsCommandTreeWithIconsAndHotbarState()
    {
        var packageIcon = new PackageIconDescriptor(null, "assets/package.svg");
        var chatIcon = new PackageIconDescriptor(null, "assets/chat.svg");
        var packageImage = new DrawingImage();
        var chatImage = new DrawingImage();
        var openedViews = new List<string>();

        var roots = ShellMenuProjector.Project(
            [
                CreateView("agent.sessions", "agent", "Sunder Agent", "Sessions", "S", RailPlacement.RightTop, packageIcon: packageIcon),
                CreateView("tools.search", "tools", "Sunder Tools", "Search", "T", RailPlacement.LeftTop),
                CreateView("agent.chat", "agent", "Sunder Agent", "Chat", "A", RailPlacement.Middle, chatIcon, packageIcon),
            ],
            (_, icon) => icon?.AssetPath switch
            {
                "assets/package.svg" => packageImage,
                "assets/chat.svg" => chatImage,
                _ => null,
            },
            viewId => string.Equals(viewId, "agent.chat", StringComparison.OrdinalIgnoreCase),
            (viewId, _) =>
            {
                openedViews.Add(viewId);
                return Task.CompletedTask;
            },
            includeDeveloperMenu: false,
            _ => Task.CompletedTask);

        var packages = Assert.Single(Assert.Single(roots).Children);
        Assert.Equal("Packages", packages.Title);
        var agent = packages.Children[0];
        Assert.Equal("package:agent", agent.Id);
        Assert.Equal("Sunder Agent", agent.Title);
        Assert.Same(packageImage, agent.IconImage);
        var chat = agent.Children[0];
        Assert.Equal("view:agent.chat", chat.Id);
        Assert.Same(chatImage, chat.IconImage);
        Assert.False(chat.IsEnabled);
        var sessions = agent.Children[1];
        Assert.True(sessions.IsEnabled);

        await sessions.ExecuteAsync!(CancellationToken.None);

        Assert.Equal(["agent.sessions"], openedViews);
    }

    [Fact]
    public void Project_NewSnapshotContainsNoRemovedEntries()
    {
        var initial = ProjectViews(
            CreateView("agent.chat", "agent", "Agent", "Chat", "A", RailPlacement.Middle),
            CreateView("tools.search", "tools", "Tools", "Search", "T", RailPlacement.LeftTop));
        var refreshed = ProjectViews(
            CreateView("tools.search", "tools", "Tools", "Search", "T", RailPlacement.LeftTop));

        Assert.Contains(Flatten(initial), item => item.Id == "package:agent");
        Assert.DoesNotContain(Flatten(refreshed), item => item.Id == "package:agent" || item.Id == "view:agent.chat");
    }

    private static IReadOnlyList<ShellMenuItem> ProjectViews(params ShellPackageView[] views)
        => ShellMenuProjector.Project(
            views,
            (_, _) => null,
            _ => false,
            (_, _) => Task.CompletedTask,
            includeDeveloperMenu: false,
            _ => Task.CompletedTask);

    private static IEnumerable<ShellMenuItem> Flatten(IEnumerable<ShellMenuItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in Flatten(item.Children))
            {
                yield return child;
            }
        }
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
}
