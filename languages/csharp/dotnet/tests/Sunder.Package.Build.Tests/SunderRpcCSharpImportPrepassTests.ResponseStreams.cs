using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpImportPrepassTests
{
    [Theory]
    [InlineData("Watch", "CompleteOnResponseContentRetirement = true", true)]
    [InlineData("Watch", "CompleteOnResponseContentRetirement = false", true)]
    [InlineData("Read", "CompleteOnResponseContentRetirement = true", false)]
    [InlineData("Names", "CompleteOnResponseContentRetirement = true", false)]
    [InlineData("Watch", "CompleteOnResponseContentRetirement = \"true\"", false)]
    [InlineData("Watch", "CompleteOnResponseContentRetirement = true, MethodId = \"other\"", false)]
    [InlineData("Watch", "CompleteOnResponseContentRetirement = true, TimeoutSeconds = 5", false)]
    public void ResponseRetirement_ImportedPolicyIsProviderLocalAndCannotOverrideLockedWireSemantics(string target, string option, bool valid)
    {
        var imports = PrepareNaturalImport("""
            using System.Collections.Generic; using System.Threading.Tasks; using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)] public interface WatchService {
                IAsyncEnumerable<SunderContent> WatchAsync();
                ValueTask<SunderContent> ReadAsync();
                IAsyncEnumerable<string> NamesAsync();
            }
            """, "watch");
        var source = $$"""
            using System.Collections.Generic; using System.Threading.Tasks; using Sunder.Sdk.Services;
            using Sunder.Generated.Packages.Example_Dependency_Services;
            [SunderServiceProvider(typeof(IWatchService))] public sealed partial class Provider {
                {{(target == "Watch" ? "[SunderMethod(" + option + ")]" : "")}}
                public IAsyncEnumerable<SunderContent> WatchAsync() => throw new System.NotImplementedException();
                {{(target == "Read" ? "[SunderMethod(" + option + ")]" : "")}}
                public ValueTask<SunderContent> ReadAsync() => throw new System.NotImplementedException();
                {{(target == "Names" ? "[SunderMethod(" + option + ")]" : "")}}
                public IAsyncEnumerable<string> NamesAsync() => throw new System.NotImplementedException();
            }
            """;
        if (valid)
        {
            var result = CompileNaturalProvider(imports, source);
            var method = Assert.Single(result.ImportedProviders).NaturalMethods.Single(method => method.MethodId == "watch");
            Assert.Equal(option.EndsWith("true", StringComparison.Ordinal), method.CompleteOnResponseContentRetirement);
            using var output = new MemoryStream();
            var emitted = result.SourceCompilation!.Emit(output);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        }
        else
        {
            var result = new SunderRpcCSharpSourceCompiler().Compile(NaturalProviderInput(imports, source));
            Assert.False(result.Success);
            Assert.NotEmpty(result.Diagnostics);
        }
    }
}
