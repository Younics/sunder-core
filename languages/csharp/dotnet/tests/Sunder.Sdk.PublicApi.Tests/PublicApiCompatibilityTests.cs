using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed partial class PublicApiCompatibilityTests
{
    // These are deliberately synthetic mutation fixtures, not alleged released SDKs
    // or prebuilt historical package evidence. The real release corpus is retained separately.
    private const string ContractName = "SunderCompatibilityMutationContract";
    private const string OldContract = """
        public abstract class Base
        {
            protected Base() { }
            protected int Seed() => 7;
        }
        public interface IEndpoint { int Read(); }
        """;
    private const string OldSource = """
        public sealed class Package : Base, IEndpoint { public int Read() => Seed(); }
        public static class Entry { public static int Run() => new Package().Read(); }
        """;

    [Fact]
    public void OldSourceAndUnmodifiedConsumerBinaryWorkWithAdditiveContract()
    {
        var oldContract = Emit(CreateCompilation(ContractName, OldContract));
        var oldBinary = Emit(CreateCompilation("SyntheticOldPackage", OldSource, oldContract));
        var newContract = Emit(CreateCompilation(ContractName, OldContract.Replace(
            "int Read();", "int Read(); int Optional() => 11;", StringComparison.Ordinal)));

        // The source is unchanged and the old binary is reused, not recompiled for the binary check.
        Emit(CreateCompilation("RecompiledOldSource", OldSource, newContract));
        Assert.Equal(7, RunConsumer(oldBinary, newContract));
    }

    [Fact]
    public void RemovingProtectedMemberBreaksBothOldSourceAndUnmodifiedBinary()
    {
        var oldContract = Emit(CreateCompilation(ContractName, OldContract));
        var oldBinary = Emit(CreateCompilation("SyntheticOldPackage", OldSource, oldContract));
        var newContract = Emit(CreateCompilation(ContractName, OldContract.Replace("protected int Seed() => 7;", "", StringComparison.Ordinal)));

        Assert.Contains(CreateCompilation("RecompiledOldSource", OldSource, newContract).GetDiagnostics(),
            static diagnostic => diagnostic.Id == "CS0103");
        var exception = Assert.Throws<TargetInvocationException>(() => RunConsumer(oldBinary, newContract));
        Assert.IsType<MissingMethodException>(exception.InnerException);
    }

    [Fact]
    public void AddingAbstractInterfaceMemberBreaksOldImplementerSource()
    {
        var oldContract = Emit(CreateCompilation(ContractName, OldContract));
        Emit(CreateCompilation("OldImplementation", OldSource, oldContract));
        var newContract = Emit(CreateCompilation(ContractName, OldContract.Replace("int Read();", "int Read(); int Mandatory();", StringComparison.Ordinal)));
        Assert.Contains(CreateCompilation("RecompiledOldSource", OldSource, newContract).GetDiagnostics(),
            static diagnostic => diagnostic.Id == "CS0535");
        Assert.Contains("method public abstract virtual System.Int32 Mandatory()", Format(newContract), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public class Api { protected int Read() => 1; }", "protected", "private", "method protected System.Int32 Read()")]
    [InlineData("public class Api { protected int value; }", "protected", "private", "field protected System.Int32 value")]
    [InlineData("public class Api { protected Api() { } }", "protected", "private", "method protected .ctor()")]
    [InlineData("public class Api { protected class Nested { } }", "protected", "private", "type protected class Api.Nested")]
    [InlineData("public class Api { public int Value { get; set; } }", "get; set;", "get; private set;", "{ public get; public set; }")]
    [InlineData("public interface Api<out T> { T Read(); }", "out T", "T", "type public interface Api<out T>")]
    [InlineData("public interface Api<in T> { void Write(T value); }", "in T", "T", "type public interface Api<in T>")]
    [InlineData("public interface Api { static abstract int Read(); }", "static abstract int Read();", "static int Read() => 1;", "method public static abstract virtual System.Int32 Read()")]
    [InlineData("public abstract class Api { public abstract int Value { get; } }", "abstract int Value { get; }", "virtual int Value => 1;", "property System.Int32 Value { public abstract virtual get; }")]
    [InlineData("public abstract class Api { public abstract event System.Action Changed; }", "abstract event", "event", "event System.Action Changed { public abstract virtual add; public abstract virtual remove; }")]
    public void FormatterCapturesExternallyObservableMutations(string source, string before, string after, string expected)
    {
        var released = Format(Emit(CreateCompilation(ContractName, source)));
        var changed = Format(Emit(CreateCompilation(ContractName, source.Replace(before, after, StringComparison.Ordinal))));
        Assert.True(released.Contains(expected, StringComparison.Ordinal), $"Missing declaration: {expected}\n{released}");
        Assert.DoesNotContain(expected, changed, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatterDistinguishesRequiredInitializersOperatorsAndNestedConstructedTypes()
    {
        var snapshot = Format(Emit(CreateCompilation(ContractName, """
            public class Outer<T> { public class Nested<U> { } }
            public class Api
            {
                public required string Name { get; init; }
                public Outer<int>.Nested<string>? Value { get; set; }
                public static implicit operator string(Api value) => value.Name;
            }
            """)));
        Assert.Contains("property required System.String Name { public get; public init; }", snapshot, StringComparison.Ordinal);
        Assert.Contains("Outer<System.Int32>.Nested<System.String>?", snapshot, StringComparison.Ordinal);
        Assert.Contains("method public static System.String op_Implicit(Api value)", snapshot, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UNFROZEN", false, true)]
    [InlineData("UNFROZEN", true, false)]
    [InlineData("FROZEN", false, false)]
    [InlineData("FROZEN", true, false)]
    [InlineData("unknown", false, false)]
    public void SnapshotUpdatesCannotBypassCiOrFrozenMode(string status, bool ci, bool allowed)
    {
        if (allowed) PublicApiSnapshotPolicy.EnsureUpdatesAllowed(status, ci);
        else Assert.Throws<InvalidOperationException>(() => PublicApiSnapshotPolicy.EnsureUpdatesAllowed(status, ci));
    }

    private static CSharpCompilation CreateCompilation(string name, string source, byte[]? contract = null)
    {
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable.")).Split(Path.PathSeparator).ToHashSet(StringComparer.Ordinal);
        paths.Add(typeof(Sunder.Sdk.Compatibility.SunderSdkCapabilityAttribute).Assembly.Location);
        var references = paths.Select(static path => MetadataReference.CreateFromFile(path)).ToList();
        if (contract is not null) references.Add(MetadataReference.CreateFromImage(contract));
        return CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static byte[] Emit(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return stream.ToArray();
    }

    private static string Format(byte[] bytes)
    {
        var context = new AssemblyLoadContext(Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            using var stream = new MemoryStream(bytes);
            return PublicApiBaselineTests.PublicApiFormatter.Format(context.LoadFromStream(stream));
        }
        finally { context.Unload(); }
    }

    private static int RunConsumer(byte[] consumer, byte[] replacementContract)
    {
        var context = new AssemblyLoadContext(Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            using var contractStream = new MemoryStream(replacementContract);
            context.LoadFromStream(contractStream);
            using var consumerStream = new MemoryStream(consumer);
            var assembly = context.LoadFromStream(consumerStream);
            return (int)assembly.GetType("Entry")!.GetMethod("Run")!.Invoke(null, null)!;
        }
        finally { context.Unload(); }
    }
}
