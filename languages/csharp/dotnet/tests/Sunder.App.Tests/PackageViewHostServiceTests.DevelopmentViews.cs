using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed partial class PackageViewHostServiceTests
{
    [Fact]
    public async Task DevelopmentViewBindingRequiresAnAlreadyCachedAttachedExactPackageView()
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(DevelopmentViewTestApplication));
        await ui.Dispatch(async () =>
        {
            using var temporary = TemporaryDirectoryScope.Create("development-view-binding");
            var registry = new AppPackageViewRegistry(TestUiDispatcher);
            await using var host = new PackageViewHostService(registry, [], [], [], temporary.RootPath, uiDispatcher: TestUiDispatcher);
            var generation = RetentionGeneration(host);
            var package = CreateActivePackage("test.view-owner");
            var source = new PackageUiSnapshotDescriptor(package.PackageId, PackageSourceKind.Dev, 4,
                new("app", "osx-arm64", "avalonia", "App.dll", "net10.0", "1.1.0", ["views.v1"]),
                new('c', 64), "snapshot", "packages/ui-snapshots/snapshot")
            { SourceContentIdentity = new('a', 64), ActivationFingerprint = new('b', 64) };
            generation.State.SetLoadedPackage(package.PackageId, new(package, source, temporary.RootPath, null, null)
            { LoadAdmission = new(Guid.NewGuid(), "test-permit") });
            var revision = new PackageDevelopmentRevision(Guid.NewGuid(), 4, source.SourceContentIdentity);
            generation.RuntimeStamp = new(revision.RuntimeInstanceId, 4);
            generation.Composition.PublishServices();
            var created = 0;
            registry.RegisterPackageView(package.PackageId, new("test.view-owner.view", "Owned view"), () => { created++; return new Panel(); });
            Assert.Throws<InvalidOperationException>(() => host.OpenDevelopmentView(package.PackageId, "test.view-owner.view", revision));
            Assert.Equal(0, created); // Inspection must not navigate, construct or preload.
            var root = host.GetOrCreateView("test.view-owner.view")!;
            Assert.Throws<InvalidOperationException>(() => host.OpenDevelopmentView(package.PackageId, "test.view-owner.view", revision));
            var window = new Window { Content = root, Width = 300, Height = 200 };
            window.Show();
            try
            {
                var binding = host.OpenDevelopmentView(package.PackageId, "test.view-owner.view", revision);
                Assert.Same(root, binding.Root);
                binding.RequireCurrent();
                Assert.Throws<InvalidOperationException>(() => host.OpenDevelopmentView("other.package", binding.ViewId, revision));
                Assert.Throws<InvalidOperationException>(() => host.OpenDevelopmentView(package.PackageId, "settings:" + package.PackageId, revision));
                Assert.Throws<InvalidOperationException>(() => host.OpenDevelopmentView(package.PackageId, binding.ViewId, new(revision.RuntimeInstanceId, 4, new('e', 64))));
                Assert.Throws<InvalidOperationException>(() => host.OpenDevelopmentView(package.PackageId, binding.ViewId, new(Guid.NewGuid(), 4, revision.SourceContentIdentity)));
                Assert.Throws<InvalidOperationException>(() => host.OpenDevelopmentView(package.PackageId, binding.ViewId, new(revision.RuntimeInstanceId, 5, revision.SourceContentIdentity)));
                Assert.Equal(1, created);
                return true;
            }
            finally { window.Close(); }
        }, default);
    }

    [Theory]
    [InlineData("detached")]
    [InlineData("hidden")]
    [InlineData("publication")]
    [InlineData("disabled")]
    [InlineData("control")]
    [InlineData("activation")]
    [InlineData("source")]
    [InlineData("fingerprint")]
    [InlineData("runtime")]
    [InlineData("generation")]
    [InlineData("installed")]
    [InlineData("readiness")]
    [InlineData("package-disabled")]
    public async Task IssuedDevelopmentViewBindingsNeverFollowAReplacedOrRetiredAuthority(string boundary)
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(DevelopmentViewTestApplication));
        await ui.Dispatch(async () =>
        {
            using var temporary = TemporaryDirectoryScope.Create("development-view-retirement");
            var registry = new AppPackageViewRegistry(TestUiDispatcher);
            await using var host = new PackageViewHostService(registry, [], [], [], temporary.RootPath, uiDispatcher: TestUiDispatcher);
            var generation = RetentionGeneration(host);
            var package = CreateActivePackage("test.view-owner");
            var source = new PackageUiSnapshotDescriptor(package.PackageId, PackageSourceKind.Dev, 4,
                new("app", "osx-arm64", "avalonia", "App.dll", "net10.0", "1.1.0", ["views.v1"]),
                new('c', 64), "snapshot", "packages/ui-snapshots/snapshot")
            { SourceContentIdentity = new('a', 64), ActivationFingerprint = new('b', 64) };
            var loaded = new AppLoadedPackageHandle(package, source, temporary.RootPath, null, null)
            { LoadAdmission = new(Guid.NewGuid(), "test-permit") };
            generation.State.SetLoadedPackage(package.PackageId, loaded);
            var revision = new PackageDevelopmentRevision(Guid.NewGuid(), 4, source.SourceContentIdentity);
            generation.RuntimeStamp = new(revision.RuntimeInstanceId, 4);
            generation.Composition.PublishServices();
            registry.RegisterPackageView(package.PackageId, new("test.view-owner.view", "Owned view"), () => new Panel());
            var root = host.GetOrCreateView("test.view-owner.view")!;
            var window = new Window { Content = root, Width = 300, Height = 200 };
            window.Show();
            try
            {
                var binding = host.OpenDevelopmentView(package.PackageId, "test.view-owner.view", revision);
                // Unrelated later observations do not revoke the exact source/activation.
                generation.RuntimeStamp = new(revision.RuntimeInstanceId, 5);
                binding.RequireCurrent();
                switch (boundary)
                {
                    case "detached": window.Content = null; break;
                    case "hidden": root.IsVisible = false; break;
                    case "publication": generation.Composition.UnpublishServices(); break;
                    case "disabled": generation.State.TryMarkPackageDisabled(package.PackageId); break;
                    case "control": registry.RemoveCachedViews(package.PackageId); host.GetOrCreateView(binding.ViewId); break;
                    case "activation": generation.State.SetLoadedPackage(package.PackageId, loaded with { LoadAdmission = new(Guid.NewGuid(), "replacement-permit") }); break;
                    case "source": generation.State.SetLoadedPackage(package.PackageId, loaded with { Source = source with { SourceContentIdentity = new('d', 64) } }); break;
                    case "fingerprint": generation.State.SetLoadedPackage(package.PackageId, loaded with { Source = source with { ActivationFingerprint = new('d', 64) } }); break;
                    case "runtime": generation.RuntimeStamp = new(Guid.NewGuid(), 5); break;
                    case "generation": generation.RuntimeStamp = new(revision.RuntimeInstanceId, 3); break;
                    case "installed": generation.State.SetLoadedPackage(package.PackageId, loaded with { Source = source with { SourceKind = PackageSourceKind.Installed } }); break;
                    case "readiness": generation.State.SetLoadedPackage(package.PackageId, loaded with { Package = package with { Readiness = PackageReadinessState.NeedsConfiguration } }); break;
                    case "package-disabled": generation.State.SetLoadedPackage(package.PackageId, loaded with { Package = package with { IsEnabled = false } }); break;
                }
                Assert.Throws<InvalidOperationException>(binding.RequireCurrent);
                return true;
            }
            finally { window.Close(); }
        }, default);
    }

    public sealed class DevelopmentViewTestApplication : Application
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<DevelopmentViewTestApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
