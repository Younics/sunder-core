using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class SunderRpcIdentifierKeywordTests
{
    private const string FixtureSha256 = "58c9633ef5f46744b039387de575b0fbfe447940a44b655ee04d56b7ed1c0241";

    [Fact]
    public void IdentifierKeywordFixture_IsCanonicalAndTargetsCSharp14()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "RpcV1Conformance",
            "identifier-keywords.json"));
        var normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        Assert.Equal(
            FixtureSha256,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSource))));
        using var document = JsonDocument.Parse(source);
        var root = document.RootElement;

        Assert.Equal(
            [
                "schemaVersion",
                "csharpLanguageVersion",
                "typescriptVersion",
                "ecmascriptTarget",
                "csharpReservedKeywords",
                "csharpContextualKeywords",
                "ecmascriptStrictModuleBindingRestrictions",
            ],
            root.EnumerateObject().Select(static property => property.Name));

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("14.0", root.GetProperty("csharpLanguageVersion").GetString());
        Assert.Equal("6.0.3", root.GetProperty("typescriptVersion").GetString());
        Assert.Equal("ES2023", root.GetProperty("ecmascriptTarget").GetString());

        var reserved = ReadArray(root, "csharpReservedKeywords");
        var contextual = ReadArray(root, "csharpContextualKeywords");
        var ecmascript = ReadArray(root, "ecmascriptStrictModuleBindingRestrictions");

        AssertCanonicalSet(reserved);
        AssertCanonicalSet(contextual);
        AssertCanonicalSet(ecmascript);
        Assert.Empty(reserved.Intersect(contextual, StringComparer.Ordinal));

        Assert.All(reserved, keyword =>
            Assert.NotEqual(SyntaxKind.None, SyntaxFacts.GetKeywordKind(keyword)));
        Assert.All(reserved.Concat(contextual), keyword =>
        {
            var token = SyntaxFactory.ParseToken("@" + keyword);
            Assert.Equal(SyntaxKind.IdentifierToken, token.Kind());
            Assert.Equal(keyword, token.ValueText);
            Assert.Empty(token.GetDiagnostics());
        });
        Assert.Contains("extension", contextual);
        Assert.Contains("field", contextual);
        Assert.DoesNotContain("closed", contextual);
        Assert.DoesNotContain("safe", contextual);
        Assert.Contains("arguments", ecmascript);
        Assert.Contains("eval", ecmascript);
        Assert.Contains("await", ecmascript);
    }

    [Fact]
    public void CSharpGenerator_EscapesEveryFrozenKeywordAndNotAdjacentIdentifiers()
    {
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "RpcV1Conformance");
        using var keywords = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            fixtureRoot,
            "identifier-keywords.json")));
        var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(
            fixtureRoot,
            "valid",
            "minimal.rpc.json")));

        foreach (var keyword in ReadArray(keywords.RootElement, "csharpReservedKeywords")
                     .Concat(ReadArray(keywords.RootElement, "csharpContextualKeywords")))
        {
            Assert.Contains(
                $"namespace @{keyword}.Generated;",
                SunderRpcCSharpGenerator.Generate(descriptor, keyword + ".Generated"),
                StringComparison.Ordinal);
            Assert.Contains(
                $"namespace {keyword}x.Generated;",
                SunderRpcCSharpGenerator.Generate(descriptor, keyword + "x.Generated"),
                StringComparison.Ordinal);
        }
    }

    private static string[] ReadArray(JsonElement root, string propertyName)
        => root.GetProperty(propertyName)
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();

    private static void AssertCanonicalSet(string[] values)
    {
        Assert.NotEmpty(values);
        Assert.Equal(values.Order(StringComparer.Ordinal), values);
        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
        Assert.All(values, value => Assert.Matches("^[a-z]+$", value));
    }
}
