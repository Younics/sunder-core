using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Tasks;

internal static class PackageWebViewBuilder
{
    public static IReadOnlyList<SunderPackageWebViewManifest> BuildForTarget(
        IReadOnlyList<ITaskItem> configuredViews,
        string packageId,
        string role,
        string rid,
        TaskLoggingHelper log)
    {
        if (!string.Equals(role, SunderPackageFormat.AppHostRole, StringComparison.Ordinal))
        {
            return [];
        }

        var output = new List<SunderPackageWebViewManifest>();
        foreach (var item in configuredViews)
        {
            var target = Metadata(item, "Target");
            var targetRid = Metadata(item, "Rid");
            if (target is not null && !string.Equals(target, $"{role}/{rid}", StringComparison.Ordinal)
                || target is null && targetRid is not null && !string.Equals(targetRid, rid, StringComparison.Ordinal))
            {
                continue;
            }

            var viewId = item.ItemSpec.Trim();
            var displayName = Metadata(item, "DisplayName");
            var route = Metadata(item, "Route");
            var icon = Metadata(item, "Icon");
            var placement = Metadata(item, "DefaultPlacement") ?? "middle";
            var hotbarText = Metadata(item, "ShowInHotbar") ?? "true";
            if (!bool.TryParse(hotbarText, out var showInHotbar))
            {
                log.LogError($"SunderPackageView '{viewId}' ShowInHotbar must be 'true' or 'false'.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(route))
            {
                log.LogError($"SunderPackageView '{viewId}' requires DisplayName and Route metadata.");
                continue;
            }
            if (!viewId.StartsWith(packageId + ".", StringComparison.Ordinal))
            {
                log.LogError($"SunderPackageView '{viewId}' must be namespaced below package id '{packageId}'.");
            }
            if (!SunderPackageFormat.IsWebViewRoute(route))
            {
                log.LogError($"SunderPackageView '{viewId}' route '{route}' is not canonical.");
            }
            if (!SunderPackageFormat.IsWebViewPlacement(placement))
            {
                log.LogError($"SunderPackageView '{viewId}' default placement '{placement}' is invalid.");
            }

            output.Add(new SunderPackageWebViewManifest
            {
                ViewId = viewId,
                DisplayName = displayName,
                Route = route,
                Icon = icon is null ? null : PackageAssetDiscovery.NormalizePath(icon),
                DefaultPlacement = placement,
                ShowInHotbar = showInHotbar,
            });
        }
        return output;
    }

    private static string? Metadata(ITaskItem item, string name)
    {
        var value = item.GetMetadata(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
