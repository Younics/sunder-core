using Microsoft.Build.Utilities;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Authoring;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class SunderPackageResourcePlannerTests
{
    [Fact]
    public void Plan_InfersLiteralEmbeddedUseAndPreservesExplicitLinkedRoles()
    {
        using var scope = TemporaryDirectoryScope.Create("sunder-resource-plan");
        var backend = new TaskItem(scope.WriteAllText("backend.bin", "backend")); backend.SetMetadata("LogicalName", "backend.data");
        var linked = new TaskItem(scope.WriteAllText("original.bin", "shared")); linked.SetMetadata("Link", "data/shared.bin");
        linked.SetMetadata("SunderPackageAssetRole", "shared"); linked.SetMetadata("CopyToOutputDirectory", "PreserveNewest");
        var input = SunderPackageResourcePlanner.Read(scope.RootPath, ("EmbeddedResource", [backend]), ("Content", [linked]));
        var plan = SunderPackageResourcePlanner.Plan(Analyze(), input, "Example");
        Assert.Equal(SunderPackageRoleMembership.Worker, Assert.Single(plan, resource => resource.Kind == "EmbeddedResource").Roles);
        var shared = Assert.Single(plan, resource => resource.Kind == "Content");
        Assert.Equal(SunderPackageRoleMembership.Shared, shared.Roles); Assert.Equal("data/shared.bin", shared.Metadata["Link"]);
        Assert.Equal("PreserveNewest", shared.Metadata["CopyToOutputDirectory"]);
    }

    [Fact]
    public void Plan_RejectsAmbiguousResourcesAndRoleConflicts()
    {
        using var scope = TemporaryDirectoryScope.Create("sunder-resource-role");
        var resource = new TaskItem(scope.WriteAllText("data.bin", "content"));
        var input = SunderPackageResourcePlanner.Read(scope.RootPath, ("EmbeddedResource", [resource]));
        Assert.Contains("resource-role-ambiguous", Assert.Throws<InvalidDataException>(() => SunderPackageResourcePlanner.Plan(Analyze(), input, "Example")).Message, StringComparison.Ordinal);
        resource.SetMetadata("LogicalName", "backend.data"); resource.SetMetadata("SunderPackageAssetRole", "app");
        input = SunderPackageResourcePlanner.Read(scope.RootPath, ("EmbeddedResource", [resource]));
        Assert.Contains("resource-role-conflict", Assert.Throws<InvalidDataException>(() => SunderPackageResourcePlanner.Plan(Analyze(), input, "Example")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsUnsafeLinksExternalResxAndXmlEntities()
    {
        using var scope = TemporaryDirectoryScope.Create("sunder-resource-invalid");
        var resource = new TaskItem(scope.WriteAllText("data.bin", "content")); resource.SetMetadata("Link", "../escape.bin");
        Assert.Contains("resource-path-invalid", Assert.Throws<InvalidDataException>(() => SunderPackageResourcePlanner.Read(scope.RootPath, ("EmbeddedResource", [resource]))).Message, StringComparison.Ordinal);
        var resx = new TaskItem(scope.WriteAllText("data.resx", "<root><data type=\"System.Resources.ResXFileRef, System.Windows.Forms\"><value>external.txt;System.String</value></data></root>"));
        Assert.Contains("resource-reference-unsupported", Assert.Throws<InvalidDataException>(() => SunderPackageResourcePlanner.Read(scope.RootPath, ("EmbeddedResource", [resx]))).Message, StringComparison.Ordinal);
        Assert.Contains("resource-xml-invalid", Assert.Throws<InvalidDataException>(() => SunderPackageResourcePlanner.Xml(
            "<!DOCTYPE root [<!ENTITY outside SYSTEM 'file:///outside'>]><root>&outside;</root>"u8.ToArray(), "fixture.axaml")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Markup_AddsDataTypeOnlyAppRootsAndRejectsWorkerImplementations()
    {
        using var scope = TemporaryDirectoryScope.Create("sunder-xaml-roles");
        var source = Analyze();
        var path = scope.WriteAllText("View.axaml", """
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:local="using:Example" x:Class="Example.View" x:DataType="local:OnlyInMarkup">
                <TextBlock Text="{Binding Label}" />
            </UserControl>
            """);
        var inputs = SunderPackageResourcePlanner.Read(scope.RootPath, ("AvaloniaXaml", [new TaskItem(path)]));
        var compilation = source.Compilation.SourceCompilation!;
        var markup = SunderPackageResourcePlanner.Markup(compilation, inputs);
        var plan = SunderPackageRolePlanner.Plan(compilation, compilation.SyntaxTrees.Where(tree => source.OrdinarySourcePaths.Contains(tree.FilePath)).ToArray(),
            source.Compilation.Contracts.SelectMany(contract => contract.SourceTypes).Select(value => value.SourceType), markup.Types);
        Assert.True(plan.Success, string.Join(Environment.NewLine, plan.Diagnostics));
        Assert.Equal(SunderPackageRoleMembership.App, Assert.Single(plan.Types, type => type.MetadataName == "global::Example.OnlyInMarkup").Roles);
        File.WriteAllText(path, File.ReadAllText(path).Replace("local:OnlyInMarkup", "local:DataService", StringComparison.Ordinal));
        markup = SunderPackageResourcePlanner.Markup(compilation, SunderPackageResourcePlanner.Read(scope.RootPath, ("AvaloniaXaml", [new TaskItem(path)])));
        plan = SunderPackageRolePlanner.Plan(compilation, compilation.SyntaxTrees.Where(tree => source.OrdinarySourcePaths.Contains(tree.FilePath)).ToArray(),
            source.Compilation.Contracts.SelectMany(contract => contract.SourceTypes).Select(value => value.SourceType), markup.Types);
        var error = Assert.Single(plan.Diagnostics, error => error.Id == "package-authoring.role-conflict");
        Assert.Equal(path, error.Path); Assert.True(error.Line > 0);
    }

    private static SunderPackageAuthoringAnalysis Analyze() => SunderPackageCompositionPlannerTests.Analyze("""
        using System.Threading.Tasks;
        using Sunder.Sdk.Packaging;
        using Sunder.Sdk.Rpc;
        namespace Example;
        [SunderRpcService]
        public sealed partial class DataService
        {
            [SunderRpcMethod] public Task<Reply> ReadAsync()
            {
                using var stream = typeof(DataService).Assembly.GetManifestResourceStream("backend.data");
                return Task.FromResult(new Reply { Number = stream!.Length });
            }
        }
        public sealed record Reply { public required long Number { get; init; } }
        [SunderPackageView("example.view", "View")]
        public sealed partial class View : Avalonia.Controls.UserControl;
        public sealed class OnlyInMarkup { public string Label => "hello"; }
        """);
}
