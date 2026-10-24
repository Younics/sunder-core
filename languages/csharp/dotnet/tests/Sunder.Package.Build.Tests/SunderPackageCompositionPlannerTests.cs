using Avalonia.Controls;
using Sunder.Package.Build.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class SunderPackageCompositionPlannerTests
{
    [Fact]
    public void Analyze_AppActivationServiceIsAnEagerAppRootWithoutAViewAndCannotBeInjectedIntoWorker()
    {
        const string activation = """
            public sealed class Startup : Sunder.Sdk.Abstractions.IPackageAppActivationService, System.IAsyncDisposable
            {
                public void Start(System.Threading.CancellationToken token) { }
                public System.Threading.Tasks.ValueTask DisposeAsync() => default;
            }
            """;
        var analysis = Analyze(Service + activation);
        Assert.True(analysis.Success, Errors(analysis));
        Assert.Empty(analysis.RolePlan.Views);
        Assert.Contains(analysis.Composition!.App, entry => entry.Type.Name == "Startup");
        Assert.DoesNotContain(analysis.Composition.Worker, entry => entry.Type.Name == "Startup");
        Assert.Contains("StartAppServices", SunderPackageCompositionEmitter.EmitApp(analysis).Text);
        Assert.Contains("()).Start(token)", SunderPackageCompositionEmitter.EmitApp(analysis).Text);
        var invalid = Analyze(Service.Replace("CounterService()", "CounterService(Startup startup)", StringComparison.Ordinal) + activation);
        Assert.False(invalid.Success);
        Assert.Contains(invalid.RolePlan.Diagnostics, error => error.Id is "package-authoring.role-conflict" or "package-authoring.di-role-conflict");
    }
    [Theory]
    [InlineData("IStore store", "public interface IStore { }", "di-unresolved")]
    [InlineData("IStore store", "public interface IStore { } public class One : IStore { } public class Two : IStore { }", "di-ambiguous")]
    [InlineData("ICounterService self", "", "di-cycle")]
    [InlineData("Helper helper", "public class Helper(ICounterService service) { public object Service => service; }", "di-cycle")]
    [InlineData("Helper helper", "public class Helper { private Helper() { } }", "di-constructor-invalid")]
    [InlineData("Helper helper", "public class Helper { public Helper() { } public Helper(long value) { } }", "di-constructor-invalid")]
    [InlineData("Helper helper", "public class Helper { [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor] private Helper() { } public Helper(long value = 1) { } }", "di-constructor-invalid")]
    [InlineData("Helper helper", "public class Helper { [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor] public Helper() { } [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor] public Helper(long value) { } }", "di-constructor-invalid")]
    [InlineData("Helper helper", "public class Helper { public Helper() { } [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor] public Helper(long value) { } }", "di-unresolved")]
    [InlineData("Helper helper", "public class Helper { public required string Name { get; init; } }", "di-constructor-invalid")]
    [InlineData("long number", "", "di-unresolved")]
    [InlineData("SunderRpcInvocationContext context", "", "di-unresolved")]
    [InlineData("System.IServiceProvider services", "", "di-unresolved")]
    [InlineData("System.Lazy<IStore> store", "public interface IStore { }", "di-unresolved")]
    [InlineData("System.Lazy<IStore> store", "public interface IStore { } public class One : IStore { } public class Two : IStore { }", "di-ambiguous")]
    [InlineData("System.Lazy<ICounterService> self", "", "di-cycle")]
    [InlineData("Sunder.Sdk.Notifications.IPackageNotificationService notifications", "", "di-unresolved")]
    [InlineData("Sunder.Sdk.Abstractions.IBackgroundTaskQueue background", "", "di-unresolved")]
    [InlineData("Sunder.Sdk.Abstractions.IPackageSettingsNavigationService navigation", "", "di-unresolved")]
    [InlineData("Sunder.Sdk.Abstractions.IPackageShellViewService shell", "", "di-unresolved")]
    [InlineData("Sunder.Sdk.Abstractions.IPackageDevelopmentService development", "", "di-unresolved")]
    [InlineData("Sunder.Sdk.Abstractions.IPackageLogNavigationService logs", "", "di-unresolved")]
    [InlineData("Sunder.Sdk.Abstractions.IPackageAppStateUpdates updates", "", "di-unresolved")]
    [InlineData("ref long number", "", "di-parameter-invalid")]
    public void Analyze_RejectsInvalidDefaultConstructorGraphsAtAuthoredSource(string parameters, string helpers, string diagnostic)
    {
        var analysis = Analyze(Service.Replace("CounterService()", "CounterService(" + parameters + ")", StringComparison.Ordinal) + helpers);
        Assert.False(analysis.Success);
        var error = Assert.Single(analysis.RolePlan.Diagnostics, value => value.Id == "package-authoring." + diagnostic);
        Assert.Equal("composition.cs", error.Path); Assert.True(error.Line > 0); Assert.True(error.Column > 0);
    }

    [Fact]
    public void Analyze_ClassifiesSettingsSchemaAsWorkerAndRejectsMultipleProviders()
    {
        const string settings = """
            public sealed class Settings : Sunder.Sdk.Worker.ISunderWorkerSettingsSchemaProvider
            {
                public Sunder.Sdk.Settings.PackageSettingsSchema SettingsSchema => throw new System.InvalidOperationException("Never run during analysis.");
            }
            """;
        var analysis = Analyze(Service + settings);
        Assert.True(analysis.Success, Errors(analysis));
        Assert.Equal(SunderPackageRoleMembership.Worker, Assert.Single(analysis.RolePlan.Types, type => type.MetadataName == "global::Example.Settings").Roles);
        Assert.Contains(analysis.Composition!.Worker, entry => entry.Type.Name == "Settings");
        var duplicate = Analyze(Service + settings + settings.Replace("class Settings", "class OtherSettings", StringComparison.Ordinal));
        Assert.Contains(duplicate.RolePlan.Diagnostics, error => error.Id == "package-authoring.settings-schema-duplicate" && error.Path == "composition.cs");
    }

    [Fact]
    public void Analyze_ClassifiesStorageMigrationRootsAndRejectsMultiplePlanProviders()
    {
        const string migrations = """
            public sealed class Migrations : Sunder.Sdk.Worker.ISunderWorkerStorageMigrationProvider,
                Sunder.Sdk.Worker.ISunderWorkerStorageMigrationPreparation
            {
                public System.Threading.Tasks.ValueTask PrepareStorageMigrationsAsync(
                    Sunder.Sdk.Worker.SunderWorkerMigrationPreparationContext context,
                    System.Threading.CancellationToken token = default) => default;
            }
            """;
        var analysis = Analyze(Service + migrations);
        Assert.True(analysis.Success, Errors(analysis));
        Assert.Equal(SunderPackageRoleMembership.Worker,
            Assert.Single(analysis.RolePlan.Types, type => type.MetadataName == "global::Example.Migrations").Roles);
        Assert.Single(analysis.Composition!.Worker, entry => entry.Type.Name == "Migrations");
        var duplicate = Analyze(Service + migrations + migrations.Replace("class Migrations", "class OtherMigrations", StringComparison.Ordinal));
        Assert.Contains(duplicate.RolePlan.Diagnostics, error =>
            error.Id == "package-authoring.storage-migration-provider-duplicate" && error.Path == "composition.cs");
    }

    [Fact]
    public void Analyze_IgnoresSourceLookalikeConstructorAttribute()
    {
        var analysis = Analyze(Service.Replace("namespace Example;", "namespace Example {", StringComparison.Ordinal)
            .Replace("CounterService()", "CounterService(Helper helper)", StringComparison.Ordinal) + """
            public sealed class Helper
            {
                [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor] public Helper() { }
                public Helper(long value) { }
            }
            }
            namespace Microsoft.Extensions.DependencyInjection
            {
                public sealed class ActivatorUtilitiesConstructorAttribute : System.Attribute;
            }
            """);
        Assert.Contains(analysis.RolePlan.Diagnostics, error => error.Id == "package-authoring.di-constructor-invalid");
    }

    [Fact]
    public void Analyze_BindsUniqueAppHelperAndOptionalDefaultsWithoutChangingRpcIdentity()
    {
        var analysis = Analyze(Service + """
            public interface IFormatter { string Format(); }
            public sealed class Formatter(long count = 4) : IFormatter { public string Format() => count.ToString(); }
            [SunderPackageView("example.view", "View", ViewModel = typeof(ViewModel))]
            public sealed class View : Avalonia.Controls.UserControl;
            public sealed class ViewModel(IFormatter formatter) { public IFormatter Formatter => formatter; }
            """);
        Assert.True(analysis.Success, Errors(analysis));
        Assert.Equal(SunderPackageRoleMembership.App, Assert.Single(analysis.RolePlan.Types, type => type.MetadataName == "global::Example.Formatter").Roles);
        var formatter = Assert.Single(analysis.Composition!.App, entry => entry.Type.Name == "Formatter");
        Assert.Empty(formatter.Arguments); Assert.False(formatter.Transient);
        Assert.Equal("Formatter", Assert.Single(Assert.Single(analysis.Composition.App, entry => entry.Type.Name == "ViewModel").Arguments).ServiceType.Name);
        Assert.Equal(Assert.Single(Analyze(Service).Compilation.Contracts).Descriptor.GetCanonicalUtf8(), Assert.Single(analysis.Compilation.Contracts).Descriptor.GetCanonicalUtf8());
    }

    [Fact]
    public void Analyze_BindsAppOnlyHostServicesForAppComposition()
    {
        var analysis = Analyze(Service + """
            [SunderPackageView("example.view", "View", ViewModel = typeof(ViewModel))]
            public sealed class View : Avalonia.Controls.UserControl;
            public sealed class ViewModel(Sunder.Sdk.Abstractions.IPackageDevelopmentService development,
                Sunder.Sdk.Abstractions.IPackageLogNavigationService logs,
                Sunder.Sdk.Abstractions.IPackageAppStateUpdates updates)
            {
                public object Development => development;
                public object Logs => logs;
                public object Updates => updates;
            }
            """);
        Assert.True(analysis.Success, Errors(analysis));
        Assert.Equal(3, Assert.Single(analysis.Composition!.App, entry => entry.Type.Name == "ViewModel").Arguments.Count);
    }

    [Fact]
    public void Analyze_RejectsSingletonCaptureOfViewModelAndMissingPublicAppUse()
    {
        var captive = Analyze(Service + """
            [SunderPackageView("example.view", "View", ViewModel = typeof(ViewModel))]
            public sealed class View : Avalonia.Controls.UserControl;
            public sealed class ViewModel(Helper helper) { public Helper Helper => helper; }
            public sealed class Helper(ViewModel viewModel) { public ViewModel Model => viewModel; }
            """);
        Assert.Contains(captive.RolePlan.Diagnostics, error => error.Id == "package-authoring.di-lifetime-conflict");
        var deferredCapture = Analyze(Service + """
            [SunderPackageView("example.view", "View", ViewModel = typeof(ViewModel))]
            public sealed class View : Avalonia.Controls.UserControl;
            public sealed class ViewModel(Helper helper) { public Helper Helper => helper; }
            public sealed class Helper(System.Lazy<ViewModel> viewModel) { public object Model => viewModel; }
            """);
        Assert.Contains(deferredCapture.RolePlan.Diagnostics, error => error.Id == "package-authoring.di-lifetime-conflict");
        var publicApi = Analyze(Service.Replace("[SunderService]", "[SunderService(SunderServiceExposure.Public)]", StringComparison.Ordinal) + """
            [SunderPackageView("example.view", "View", ViewModel = typeof(ViewModel))]
            public sealed class View : Avalonia.Controls.UserControl;
            public sealed class ViewModel(ICounterService counter) { public ICounterService Counter => counter; }
            """);
        Assert.True(publicApi.Success, Errors(publicApi));
    }

    [Fact]
    public void Analyze_HostContextDependencyRequiresExplicitBrokerUseAndDoesNotBecomeALocalCycle()
    {
        var source = Service.Replace("CounterService()", "CounterService(ICounterService self)", StringComparison.Ordinal)
            .Replace("GetAsync()", "GetAsync(SunderRpcInvocationContext context)", StringComparison.Ordinal);
        var denied = Analyze(source);
        Assert.Contains(denied.RolePlan.Diagnostics, error => error.Id == "package-authoring.di-context-required");
        var allowed = Analyze(source.Replace("[SunderService]", "[SunderRuntimeUse]\n[SunderService]", StringComparison.Ordinal));
        Assert.True(allowed.Success, Errors(allowed));
        Assert.NotNull(Assert.Single(allowed.Compilation.Contracts).RuntimeUse);
        Assert.Single(allowed.Composition!.Worker);
    }

    [Fact]
    public void Analyze_RejectsExcessiveConstructorDepthBeforeEmission()
    {
        var source = Service.Replace("CounterService()", "CounterService(Node0 value)", StringComparison.Ordinal)
            + string.Join("\n", Enumerable.Range(0, 130).Select(index => index == 129 ? "public class Node129 { }"
                : $"public class Node{index}(Node{index + 1} next) {{ public object Next => next; }}"));
        Assert.Contains(Analyze(source).RolePlan.Diagnostics, error => error.Id == "package-authoring.di-limit-exceeded");
    }

    internal static SunderPackageAuthoringAnalysis Analyze(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat([typeof(SunderPackageCodeRoleAttribute).Assembly.Location, typeof(SunderWorker).Assembly.Location,
                typeof(Control).Assembly.Location, typeof(Avalonia.AvaloniaObject).Assembly.Location]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return SunderPackageAuthoringCompiler.Analyze(new("example.notes", "1.0.0", [new("composition.cs", source)], references, AssemblyName: "Example.Composition"));
    }
    private static string Errors(SunderPackageAuthoringAnalysis value) => string.Join(Environment.NewLine,
        value.Compilation.Diagnostics.Concat(value.RolePlan.Diagnostics).Select(error => error.Message));
    private const string Service = """
        using System.Threading.Tasks;
        using Sunder.Sdk.Packaging;
        using Sunder.Sdk.Services;
        using Sunder.Sdk.Rpc;
        using Sunder.Generated.Packages.Example_Notes_Services;
        namespace Example;
        [SunderService]
        public sealed partial class CounterService()
        {
            public Task<Reply> GetAsync() => Task.FromResult(new Reply { Number = 1 });
        }
        public sealed record Reply { public required long Number { get; init; } }

        """;
}
