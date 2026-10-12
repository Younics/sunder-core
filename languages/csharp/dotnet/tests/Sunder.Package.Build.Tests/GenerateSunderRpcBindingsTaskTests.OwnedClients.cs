using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class GenerateSunderRpcBindingsTaskTests
{
    [Fact]
    public void Execute_BootstrapsOwnedClientsFromCurrentSourceWithoutPriorOutputOrImplicitGrants()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "Program.cs");
            var references = CompilationReferences();
            var descriptor = Assert.Single(new SunderRpcCSharpSourceCompiler().Compile(new(
                "", "7.6.5", [new(path, ImplementationFirstSource)], references)).Contracts).Descriptor;
            WriteSourceAuthoringState(root, descriptor);
            File.Delete(Path.Combine(root, "sunder-contracts.owned.json"));
            File.WriteAllText(path, ImplementationFirstSource + OwnedCaller);
            var accept = CreateSourceTask(root, Path.Combine(root, "obj"), path, references);
            accept.ContractOperation = "Accept";
            Assert.True(accept.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)accept.BuildEngine).Errors));
            var generate = CreateSourceTask(root, Path.Combine(root, "obj"), path, references);
            Assert.True(generate.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)generate.BuildEngine).Errors));
            Assert.Contains(generate.CompileItems, item => Path.GetFileName(item.ItemSpec) == "package-api.g.cs");
            Assert.Empty(generate.RpcUses);
            Assert.Equal(descriptor.Sha256, Assert.Single(generate.BindingSnapshots).GetMetadata("DescriptorSha256"));
            using var receipt = JsonDocument.Parse(File.ReadAllBytes(Assert.Single(generate.AuthoringReceipts).ItemSpec));
            Assert.Empty(receipt.RootElement.GetProperty("uses").EnumerateArray());
            Assert.Empty(receipt.RootElement.GetProperty("packageApiImports").EnumerateArray());
            var before = ReadOwnedSet(Path.Combine(root, "obj/sunder-rpc"));
            Assert.True(generate.Execute());
            Assert.Equal(before, ReadOwnedSet(Path.Combine(root, "obj/sunder-rpc")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Execute_OwnedClientPrepassUsesNewSourceMethodsInsteadOfTheAcceptedDescriptor()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "Program.cs");
            var references = CompilationReferences();
            var descriptor = Assert.Single(new SunderRpcCSharpSourceCompiler().Compile(new(
                "", "7.6.5", [new(path, ImplementationFirstSource)], references)).Contracts).Descriptor;
            WriteSourceAuthoringState(root, descriptor);
            var changed = ImplementationFirstSource.Replace("public sealed partial class GreetingService\n{", "public sealed partial class GreetingService\n{\n"
                + "    public ValueTask<GreetingResult> SecondAsync(GreetingRequest request, CancellationToken token) => ValueTask.FromResult(new GreetingResult { Message = request.Name });", StringComparison.Ordinal);
            Assert.NotEqual(ImplementationFirstSource, changed);
            File.WriteAllText(path, changed + OwnedCaller.Replace(".GreetAsync(", ".SecondAsync(", StringComparison.Ordinal));
            var accept = CreateSourceTask(root, Path.Combine(root, "obj"), path, references);
            accept.ContractOperation = "Accept";
            Assert.True(accept.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)accept.BuildEngine).Errors));
            var generate = CreateSourceTask(root, Path.Combine(root, "obj"), path, references);
            Assert.True(generate.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)generate.BuildEngine).Errors));
            var nextHash = Assert.Single(generate.BindingSnapshots).GetMetadata("DescriptorSha256");
            Assert.Equal(64, nextHash.Length);
            Assert.NotEqual(descriptor.Sha256, nextHash);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Execute_OwnedClientPrepassStillRejectsInvalidBodiesWithoutChangingOutputOrBaseline(bool unknownClientMethod)
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "Program.cs");
            var references = CompilationReferences();
            var descriptor = Assert.Single(new SunderRpcCSharpSourceCompiler().Compile(new(
                "", "7.6.5", [new(path, ImplementationFirstSource)], references)).Contracts).Descriptor;
            WriteSourceAuthoringState(root, descriptor);
            File.WriteAllText(path, ImplementationFirstSource + OwnedCaller);
            var generate = CreateSourceTask(root, Path.Combine(root, "obj"), path, references);
            Assert.True(generate.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)generate.BuildEngine).Errors));
            var before = ReadOwnedSet(Path.Combine(root, "obj/sunder-rpc"));
            var baseline = File.ReadAllBytes(Path.Combine(root, "sunder-contracts.owned.json"));
            File.WriteAllText(path, ImplementationFirstSource + (unknownClientMethod
                ? OwnedCaller.Replace(".GreetAsync(", ".MissingAsync(", StringComparison.Ordinal)
                : OwnedCaller + "\ninternal static class InvalidBody { internal static int Value => \"wrong type\"; }"));
            var failed = CreateSourceTask(root, Path.Combine(root, "obj"), path, references);
            failed.ContractOperation = "Accept";
            Assert.False(failed.Execute());
            Assert.NotEmpty(((TestBuildEngine)failed.BuildEngine).Errors);
            Assert.Equal(before, ReadOwnedSet(Path.Combine(root, "obj/sunder-rpc")));
            Assert.Equal(baseline, File.ReadAllBytes(Path.Combine(root, "sunder-contracts.owned.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private const string OwnedCaller = """

        internal sealed class OwnedCaller(Sunder.Sdk.Rpc.ISunderRpcClient rpc)
        {
            internal async System.Threading.Tasks.ValueTask<string> Call(System.Threading.CancellationToken token)
            {
                var api = new Sunder.Generated.Packages.Example_Generated_Internal.PackageApi(rpc).Greeting;
                var catalog = await api.ListAsync(token);
                await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token);
                var reply = await api.CreateClient(scope, catalog.Providers[0]).SGreeting.GreetAsync(new() { Name = "hello" }, token);
                return reply.Message;
            }
        }
        """;
}
