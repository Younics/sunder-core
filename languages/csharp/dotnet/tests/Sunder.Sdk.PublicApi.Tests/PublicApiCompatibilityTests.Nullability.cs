using Microsoft.CodeAnalysis;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed partial class PublicApiCompatibilityTests
{
    [Fact]
    public void EventHandlerAndPayloadNullabilityAreDistinctSourceContracts()
    {
        const string nullableSource = """
            public class Api
            {
                public event System.Action<string?>? Changed { add { } remove { } }
            }
            """;
        var nullable = Emit(CreateCompilation(ContractName, nullableSource));
        var nonnullableHandler = Emit(CreateCompilation(ContractName,
            nullableSource.Replace("Action<string?>?", "Action<string?>", StringComparison.Ordinal)));
        var nonnullablePayload = Emit(CreateCompilation(ContractName,
            nullableSource.Replace("Action<string?>?", "Action<string>?", StringComparison.Ordinal)));

        var nullableSnapshot = Format(nullable);
        Assert.Contains("event System.Action<System.String?>? Changed", nullableSnapshot, StringComparison.Ordinal);
        Assert.Contains("event System.Action<System.String?> Changed", Format(nonnullableHandler), StringComparison.Ordinal);
        Assert.Contains("event System.Action<System.String>? Changed", Format(nonnullablePayload), StringComparison.Ordinal);
        Assert.NotEqual(nullableSnapshot, Format(nonnullableHandler));
        Assert.NotEqual(nullableSnapshot, Format(nonnullablePayload));

        // An unchanged handler that requires a non-null payload cannot safely consume
        // an event whose delegate was changed to permit nullable payloads.
        AssertNullabilitySourceBreak(nonnullablePayload, nullable, """
            public static class Consumer
            {
                public static void Attach(Api api) => api.Changed += Handle;
                private static void Handle(string value) { }
            }
            """, "CS8622");
    }

    [Theory]
    [InlineData("property", "AllowNull")]
    [InlineData("field", "AllowNull")]
    [InlineData("init", "AllowNull")]
    [InlineData("write-only", "AllowNull")]
    [InlineData("property", "DisallowNull")]
    [InlineData("field", "DisallowNull")]
    public void TighteningAcceptedNullInputChangesSnapshotWithoutChangingReadType(string kind, string annotation)
    {
        var removesAllowNull = annotation == "AllowNull";
        var type = removesAllowNull ? "string" : "string?";
        var member = kind switch
        {
            "field" => $"public {type} Value = string.Empty;",
            "init" => $"public {type} Value {{ get => string.Empty; init {{ }} }}",
            "write-only" => $"public {type} Value {{ set {{ }} }}",
            _ => $"public {type} Value {{ get => string.Empty; set {{ }} }}",
        };
        var attribute = $"[System.Diagnostics.CodeAnalysis.{annotation}]";
        var before = Emit(CreateCompilation(ContractName,
            $"public class Api {{ {(removesAllowNull ? attribute : string.Empty)} {member} }}"));
        var after = Emit(CreateCompilation(ContractName,
            $"public class Api {{ {(removesAllowNull ? string.Empty : attribute)} {member} }}"));
        var beforeSnapshot = Format(before);
        var afterSnapshot = Format(after);
        var declaration = $"{(kind == "field" ? "field public" : "property")} System.String{(removesAllowNull ? "" : "?")} Value";
        Assert.Contains(declaration, beforeSnapshot, StringComparison.Ordinal);
        Assert.Contains(declaration, afterSnapshot, StringComparison.Ordinal);
        if (removesAllowNull)
        {
            Assert.Contains("[write: System.String?]", beforeSnapshot, StringComparison.Ordinal);
            Assert.DoesNotContain("[write:", afterSnapshot, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("[write:", beforeSnapshot, StringComparison.Ordinal);
            Assert.Contains("[write: System.String]", afterSnapshot, StringComparison.Ordinal);
        }

        AssertNullabilitySourceBreak(before, after,
            "public static class Consumer { public static Api Create() => new Api { Value = null }; }",
            "CS8625", "CS8601");
    }

    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("init")]
    [InlineData("add")]
    [InlineData("remove")]
    public void SupportedContractAttributesAreRecordedOnEachAccessor(string accessor)
    {
        const string attributes = """
            [Sunder.Sdk.Compatibility.SunderSdkCapability("storage.v1")]
            [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
            """;
        var original = Format(Emit(CreateCompilation(ContractName, AccessorFixture(accessor, attributes))));
        var removed = Format(Emit(CreateCompilation(ContractName, AccessorFixture(accessor, string.Empty))));
        var changed = Format(Emit(CreateCompilation(ContractName,
            AccessorFixture(accessor, attributes.Replace("storage.v1", "logging.v1", StringComparison.Ordinal)))));

        var capability = $"  attribute {accessor} Sunder.Sdk.Compatibility.SunderSdkCapabilityAttribute(\"storage.v1\")";
        var editorBrowsable = $"  attribute {accessor} System.ComponentModel.EditorBrowsableAttribute(Advanced)";
        Assert.Contains(capability, original, StringComparison.Ordinal);
        Assert.Contains(editorBrowsable, original, StringComparison.Ordinal);
        Assert.DoesNotContain(capability, removed, StringComparison.Ordinal);
        Assert.DoesNotContain(editorBrowsable, removed, StringComparison.Ordinal);
        Assert.DoesNotContain(capability, changed, StringComparison.Ordinal);
        Assert.Contains($"  attribute {accessor} Sunder.Sdk.Compatibility.SunderSdkCapabilityAttribute(\"logging.v1\")", changed, StringComparison.Ordinal);
    }

    [Fact]
    public void MovingCapabilityBetweenGetterAndSetterCannotKeepTheSameContractRecord()
    {
        const string capability = "[Sunder.Sdk.Compatibility.SunderSdkCapability(\"storage.v1\")]";
        var getter = Format(Emit(CreateCompilation(ContractName, AccessorFixture("get", capability))));
        var setter = Format(Emit(CreateCompilation(ContractName, AccessorFixture("set", capability))));
        Assert.Contains("attribute get Sunder.Sdk.Compatibility.SunderSdkCapabilityAttribute", getter, StringComparison.Ordinal);
        Assert.DoesNotContain("attribute set Sunder.Sdk.Compatibility.SunderSdkCapabilityAttribute", getter, StringComparison.Ordinal);
        Assert.Contains("attribute set Sunder.Sdk.Compatibility.SunderSdkCapabilityAttribute", setter, StringComparison.Ordinal);
        Assert.DoesNotContain("attribute get Sunder.Sdk.Compatibility.SunderSdkCapabilityAttribute", setter, StringComparison.Ordinal);
        Assert.NotEqual(getter, setter);
    }

    [Fact]
    public void NullableValueContainersPreserveNestedReferenceNullabilityInSourceContracts()
    {
        const string type = "System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.KeyValuePair<int, System.Collections.Generic.List<string?>?>?>?";
        var source = $$"""
            public class Api
            {
                public {{type}} Value { get; set; }
                public {{type}} Field;
                public {{type}} RoundTrip({{type}} value) => value;
            }
            """;
        var before = Emit(CreateCompilation(ContractName, source));
        var after = Emit(CreateCompilation(ContractName, source.Replace("List<string?>", "List<string>", StringComparison.Ordinal)));
        const string expectedType = "System.Collections.Generic.KeyValuePair<System.String, System.Collections.Generic.KeyValuePair<System.Int32, System.Collections.Generic.List<System.String?>?>?>?";
        var snapshot = Format(before);
        Assert.Contains($"property {expectedType} Value", snapshot, StringComparison.Ordinal);
        Assert.Contains($"field public {expectedType} Field", snapshot, StringComparison.Ordinal);
        Assert.Contains($"method public {expectedType} RoundTrip({expectedType} value)", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain(expectedType, Format(after), StringComparison.Ordinal);

        AssertNullabilitySourceBreak(before, after, """
            public static class Consumer
            {
                public static void Write(Api api) => api.Value?.Value?.Value?.Add(null);
            }
            """, "CS8625");
    }

    private static string AccessorFixture(string accessor, string attributes)
        => accessor is "add" or "remove"
            ? $$"""
                public class Api
                {
                    public event System.Action Changed
                    {
                        {{(accessor == "add" ? attributes : "")}} add { }
                        {{(accessor == "remove" ? attributes : "")}} remove { }
                    }
                }
                """
            : $$"""
                public class Api
                {
                    public string Value
                    {
                        {{(accessor == "get" ? attributes : "")}} get => string.Empty;
                        {{(accessor is "set" or "init" ? attributes : "")}} {{(accessor == "init" ? "init" : "set")}} { }
                    }
                }
                """;

    private static void AssertNullabilitySourceBreak(byte[] before, byte[] after, string source, params string[] diagnosticIds)
    {
        var original = CreateCompilation("NullabilityConsumer", source, before);
        Emit(original.WithOptions(original.Options.WithGeneralDiagnosticOption(ReportDiagnostic.Error)));
        var changed = CreateCompilation("NullabilityConsumer", source, after);
        var diagnostics = changed.WithOptions(changed.Options.WithGeneralDiagnosticOption(ReportDiagnostic.Error)).GetDiagnostics();
        Assert.Contains(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error
            && diagnosticIds.Contains(diagnostic.Id, StringComparer.Ordinal));
    }
}
