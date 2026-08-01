using Sunder.Sdk.Packaging;

namespace Sunder.Package.Format;

internal static class PackageWebViewValidator
{
    public static void ValidateDeclarations(
        SunderPackageManifest manifest,
        ICollection<string> errors,
        string prefix)
    {
        var declared = new Dictionary<string, (string ExactId, SunderPackageWebViewManifest View)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var target in (manifest.Targets ?? []).Take(SunderPackageFormat.MaxTargets))
        {
            if (target is null)
            {
                continue;
            }

            var views = target.Views;
            var key = DisplayKey(target);
            if (string.Equals(target.Kind, SunderPackageFormat.WebTargetKind, StringComparison.Ordinal))
            {
                if (views is null || views.Count == 0)
                {
                    errors.Add($"{prefix} web target '{key}' must declare at least one view.");
                    continue;
                }
            }
            else if (views is { Count: > 0 })
            {
                errors.Add($"{prefix} target '{key}' kind '{target.Kind}' cannot declare web views.");
                continue;
            }

            if (views is null)
            {
                continue;
            }
            if (views.Count > SunderPackageFormat.MaxWebViewsPerTarget)
            {
                errors.Add(
                    $"{prefix} target '{key}' declares {views.Count} web views; the limit is {SunderPackageFormat.MaxWebViewsPerTarget}.");
            }

            var localIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var localRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var view in views.Take(SunderPackageFormat.MaxWebViewsPerTarget))
            {
                if (view is null)
                {
                    errors.Add($"{prefix} target '{key}' contains a null web view.");
                    continue;
                }

                var validId = ValidateView(
                    manifest.Id,
                    key,
                    view,
                    localIds,
                    localRoutes,
                    errors,
                    prefix);
                if (!validId)
                {
                    continue;
                }

                if (declared.TryGetValue(view.ViewId!, out var previous))
                {
                    if (!string.Equals(previous.ExactId, view.ViewId, StringComparison.Ordinal))
                    {
                        errors.Add(
                            $"{prefix} web view id '{view.ViewId}' has a case collision with '{previous.ExactId}'.");
                    }
                    else if (!Equivalent(previous.View, view))
                    {
                        errors.Add(
                            $"{prefix} web view '{view.ViewId}' must have identical metadata in every exact target.");
                    }
                }
                else
                {
                    declared.Add(view.ViewId!, (view.ViewId!, view));
                }
            }
        }
    }

    public static void ValidateResolvedIcons(
        SunderPackageTargetManifest target,
        SunderPackageProjectionPlan plan,
        ICollection<string> errors,
        string prefix)
    {
        foreach (var view in target.Views ?? [])
        {
            if (view?.Icon is null
                || !ArchiveRelativePath.TryParse(
                    view.Icon,
                    SunderPackageFormat.MaxLogicalPathLength,
                    SunderPackageFormat.MaxLogicalPathDepth,
                    out var icon,
                    out _))
            {
                continue;
            }
            if (!plan.TryResolveLogicalPath(icon, out _))
            {
                errors.Add(
                    $"{prefix} web view '{view.ViewId ?? "unknown"}' icon '{view.Icon}' does not resolve in target '{plan.Target}' payload union.");
            }
        }
    }

    private static bool ValidateView(
        string? packageId,
        string targetKey,
        SunderPackageWebViewManifest view,
        ISet<string> localIds,
        ISet<string> localRoutes,
        ICollection<string> errors,
        string prefix)
    {
        var validId = PackageId.TryParse(view.ViewId, out _)
                      && !string.IsNullOrEmpty(packageId)
                      && view.ViewId!.StartsWith(packageId + ".", StringComparison.Ordinal);
        if (!validId)
        {
            errors.Add(
                $"{prefix} target '{targetKey}' web view id '{view.ViewId}' must be a lowercase package id namespaced below '{packageId}'.");
        }
        else if (!localIds.Add(view.ViewId!))
        {
            errors.Add(
                $"{prefix} target '{targetKey}' declares duplicate or case-colliding web view id '{view.ViewId}'.");
            validId = false;
        }

        if (string.IsNullOrWhiteSpace(view.DisplayName)
            || view.DisplayName.Length > SunderPackageFormat.MaxWebViewDisplayNameLength
            || view.DisplayName != view.DisplayName.Trim()
            || view.DisplayName.Any(char.IsControl))
        {
            errors.Add(
                $"{prefix} target '{targetKey}' web view '{view.ViewId ?? "unknown"}' displayName must be trimmed, non-empty, and at most {SunderPackageFormat.MaxWebViewDisplayNameLength} characters.");
        }
        if (!SunderPackageFormat.IsWebViewRoute(view.Route))
        {
            errors.Add(
                $"{prefix} target '{targetKey}' web view '{view.ViewId ?? "unknown"}' route '{view.Route}' is not a canonical origin-relative route.");
        }
        else if (!localRoutes.Add(view.Route!))
        {
            errors.Add(
                $"{prefix} target '{targetKey}' declares duplicate or case-colliding web view route '{view.Route}'.");
        }
        if (view.Icon is not null
            && !ArchiveRelativePath.TryParse(
                view.Icon,
                SunderPackageFormat.MaxLogicalPathLength,
                SunderPackageFormat.MaxLogicalPathDepth,
                out _,
                out var iconError))
        {
            errors.Add(
                $"{prefix} target '{targetKey}' web view '{view.ViewId ?? "unknown"}' icon path is unsafe: {iconError}.");
        }
        if (!SunderPackageFormat.IsWebViewPlacement(view.DefaultPlacement))
        {
            errors.Add(
                $"{prefix} target '{targetKey}' web view '{view.ViewId ?? "unknown"}' has invalid defaultPlacement '{view.DefaultPlacement}'.");
        }
        if (view.ShowInHotbar is null)
        {
            errors.Add(
                $"{prefix} target '{targetKey}' web view '{view.ViewId ?? "unknown"}' is missing showInHotbar.");
        }
        return validId;
    }

    private static bool Equivalent(SunderPackageWebViewManifest left, SunderPackageWebViewManifest right)
        => string.Equals(left.ViewId, right.ViewId, StringComparison.Ordinal)
           && string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal)
           && string.Equals(left.Route, right.Route, StringComparison.Ordinal)
           && string.Equals(left.Icon, right.Icon, StringComparison.Ordinal)
           && string.Equals(left.DefaultPlacement, right.DefaultPlacement, StringComparison.Ordinal)
           && left.ShowInHotbar == right.ShowInHotbar;

    private static string DisplayKey(SunderPackageTargetManifest target)
        => $"{target.Role ?? "unknown"}/{target.Rid ?? "unknown"}";
}
