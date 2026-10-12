using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpModelProjectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionOnlyUnitRootsHaveConcreteOwnedBindingsAndKeepImportedWireDtos(bool reverseMethods)
    {
        var source = """
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            namespace Units;
            [SunderService(SunderServiceExposure.Public)] public interface CommandsService { ValueTask ExecuteAsync(string request); }
            [SunderServiceProvider(typeof(CommandsService))] public sealed partial class CommandsProvider : CommandsService {
                public ValueTask ExecuteAsync(string request) => ValueTask.CompletedTask;
            }
            [SunderService] internal sealed partial class PrivateService {
                public ValueTask ExecuteAsync(string request) => ValueTask.CompletedTask;
            }
            [SunderService] public sealed partial class MixedService {
                public ValueTask ExecuteAsync(string request) => ValueTask.CompletedTask;
                public ValueTask FlushAsync() => ValueTask.CompletedTask;
            }
            """;
        if (reverseMethods) source = source.Replace("public ValueTask ExecuteAsync(string request) => ValueTask.CompletedTask;\n    public ValueTask FlushAsync() => ValueTask.CompletedTask;",
            "public ValueTask FlushAsync() => ValueTask.CompletedTask;\n    public ValueTask ExecuteAsync(string request) => ValueTask.CompletedTask;", StringComparison.Ordinal);
        var input = new SunderRpcCSharpCompilerInput("example.shared", "1.0.0", [new("source.cs", source)], References(), GenerateServiceInterfaces: true);
        var prepass = SunderRpcCSharpOwnedClientPrepass.Compile(new(), input);
        Assert.True(prepass.Compilation.Success, string.Join(Environment.NewLine, prepass.Compilation.Diagnostics));
        Assert.Equal(reverseMethods ? "flush" : "execute", prepass.Compilation.Contracts.Single(contract => contract.ContractKey == "mixed").Methods[0].MethodId);
        foreach (var contract in prepass.Compilation.Contracts)
        {
            var plan = SunderRpcCSharpMarshallingPlan.Owned(contract);
            foreach (var method in contract.Methods.Where(method => method.ReturnsUnit))
            {
                Assert.NotEqual("Unit", plan.Layouts[method.OutputDefinition].TypeName);
                Assert.True(contract.Descriptor.IsValid("#/$defs/" + method.OutputDefinition, JsonSerializer.Deserialize<JsonElement>("{}"), out _));
                Assert.False(contract.Descriptor.IsValid("#/$defs/" + method.OutputDefinition, JsonSerializer.Deserialize<JsonElement>("{\"extra\":true}"), out _));
            }
        }
        var owned = prepass.Sources.Append(new SunderRpcCSharpSourceFile("source.cs", source))
            .Append(new("authoring.g.cs", SunderRpcCSharpImplementationEmitter.Emit(prepass.Compilation.Contracts)))
            .Concat(SunderRpcCSharpServiceApiEmitter.EmitAppClients(prepass.Compilation.Contracts, "example.shared", "1.0.0", providerSchemasPresent: true));
        AssertCompiles(owned);
        var (load, probe) = LoadProjectionProbe(owned.Append(new("unit-probe.cs", """
            using System.Text.Json;
            using System.Threading.Tasks;
            using Xunit;
            public static class UnitProbe {
                public static async Task Run() {
                    var privateHandler = new Units.PrivateService().CreateSunderRpcProviderRegistration().Handler;
                    var publicHandler = new Units.CommandsProvider().CreateSunderRpcProviderRegistration().Handler;
                    var mixedHandler = new Units.MixedService().CreateSunderRpcProviderRegistration().Handler;
                    var text = JsonSerializer.SerializeToElement("request");
                    var empty = JsonSerializer.Deserialize<JsonElement>("{}");
                    Assert.Empty((await privateHandler.InvokeUnaryAsync(null!, "private", "execute", text)).EnumerateObject());
                    Assert.Empty((await publicHandler.InvokeUnaryAsync(null!, "commands", "execute", text)).EnumerateObject());
                    Assert.Empty((await mixedHandler.InvokeUnaryAsync(null!, "mixed", "execute", text)).EnumerateObject());
                    Assert.Empty((await mixedHandler.InvokeUnaryAsync(null!, "mixed", "flush", empty)).EnumerateObject());
                }
            }
            """)), "UnitProbe");
        try { await probe.GetMethod("Run")!.CreateDelegate<Func<Task>>()(); }
        finally { load.Unload(); }
        var imported = SunderRpcCSharpImportPrepass.PrepareOwnedPackageApi(ProjectExport(prepass.Compilation), "1.0.0");
        AssertCompiles(imported);
        Assert.Contains(imported, file => file.Path.StartsWith("models/", StringComparison.Ordinal) && file.Text.Contains("_Unit", StringComparison.Ordinal));
    }
}
