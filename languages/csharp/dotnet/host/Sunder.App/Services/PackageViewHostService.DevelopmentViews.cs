using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

public sealed partial class PackageViewHostService
{
    internal IReadOnlyList<PackageDevelopmentView> GetDevelopmentViews(string packageId, PackageDevelopmentRevision revision)
    {
        ThrowIfDisposed();
        var current = CurrentGeneration;
        var package = current.State.GetLoadedPackage(packageId);
        if (!_uiDispatcher.CheckAccess() || !current.Composition.ServicesPublished || package?.Source.SourceKind != PackageSourceKind.Dev
            || package.Source.SourceContentIdentity != revision.SourceContentIdentity || package.LoadAdmission?.ActivationId is null
            || current.RuntimeStamp is not { } stamp || stamp.RuntimeInstanceId != revision.RuntimeInstanceId || stamp.SessionGeneration < revision.SessionGeneration)
            throw new InvalidOperationException("The original development App revision is unavailable.");
        return current.ViewRegistry.GetPackageViewDescriptors(packageId).Take(64).Select(view =>
        {
            var root = current.ViewRegistry.GetCachedPackageView(packageId, view.ViewId);
            var open = root is { IsEffectivelyVisible: true } && Avalonia.Controls.TopLevel.GetTopLevel(root) is not null;
            return new PackageDevelopmentView(view.ViewId, view.Title, open) { CanCapture = open && (OperatingSystem.IsMacOS()
                || !Avalonia.VisualTree.VisualExtensions.GetSelfAndVisualDescendants(root!).Any(control => control is IAppDevelopmentDocumentOwner)) };
        }).ToArray();
    }

    internal AppDevelopmentViewBinding OpenDevelopmentView(string packageId, string viewId, PackageDevelopmentRevision revision)
    {
        ThrowIfDisposed();
        if (!_uiDispatcher.CheckAccess()) throw new InvalidOperationException("Development view binding requires the App UI dispatcher.");
        var generation = CurrentGeneration;
        var loaded = generation.State.GetLoadedPackage(packageId);
        var activation = loaded?.LoadAdmission?.ActivationId;
        var root = generation.ViewRegistry.GetCachedPackageView(packageId, viewId);
        if (loaded is null || activation is null || activation == Guid.Empty || root is null
            || string.IsNullOrWhiteSpace(loaded.Source.ActivationFingerprint))
            throw new InvalidOperationException("Open a view owned by this exact development App activation before inspecting it.");
        void RequireCurrent()
        {
            ThrowIfDisposed();
            var current = CurrentGeneration;
            var package = current.State.GetLoadedPackage(packageId);
            if (!current.Composition.ServicesPublished || current.State.IsPackageDisabled(packageId)
                || Avalonia.Controls.TopLevel.GetTopLevel(root) is null || !root.IsEffectivelyVisible
                || current.RuntimeStamp is not { } stamp || stamp.RuntimeInstanceId != revision.RuntimeInstanceId
                || stamp.SessionGeneration < revision.SessionGeneration
                || package?.Source.SourceKind != PackageSourceKind.Dev
                || !package.Package.IsEnabled || package.Package.Readiness is not (PackageReadinessState.Ready or PackageReadinessState.Degraded)
                || package.Source.SourceContentIdentity != revision.SourceContentIdentity
                || package.Source.ActivationFingerprint != loaded.Source.ActivationFingerprint
                || package.LoadAdmission?.ActivationId != activation
                || !ReferenceEquals(current.ViewRegistry.GetCachedPackageView(packageId, viewId), root))
                throw new InvalidOperationException("The original development view activation is unavailable. Inspect the current view; never replay an old input against a replacement.");
        }
        RequireCurrent();
        return new(packageId, viewId, revision, root, _uiDispatcher, RequireCurrent);
    }
}
