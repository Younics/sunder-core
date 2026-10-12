using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Sunder.Package.Build.Rpc.Authoring.CSharp;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpSourceCompilerTests
{
    // Frozen source is historical evidence, not a supported authoring dialect.
    // Project its declaration tokens into the sole current API only in this oracle
    // reader. Preserve the captured files/JSON and translate diagnostic positions
    // back to their original text rather than rewriting the historical span oracle.
    private sealed class HistoricalSourceProjection(string text)
    {
        private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SunderRpcService"] = "SunderService", ["SunderRpcExposure"] = "SunderServiceExposure",
            ["SunderRpcProvider"] = "SunderServiceProvider", ["SunderRpcProviderAudience"] = "SunderServiceAudience",
            ["SunderRpcPlatformImport"] = "SunderPlatformImport", ["SunderRpcMethod"] = "SunderMethod",
            ["SunderRpcMethodIdempotency"] = "SunderServiceIdempotency", ["SunderRpcDomainError"] = "SunderServiceError",
            ["SunderRpcDefinition"] = "SunderValue", ["SunderRpcFacet"] = "SunderValueConstraints",
            ["SunderRpcTransparent"] = "SunderTransparentValue", ["SunderRpcNumericEnum"] = "SunderNumericEnum",
            ["SunderRpcUnion"] = "SunderUnion", ["SunderRpcDiscriminator"] = "SunderDiscriminator",
            ["SunderRpcVariant"] = "SunderVariant", ["SunderRpcAppUse"] = "SunderAppUse", ["SunderRpcRuntimeUse"] = "SunderRuntimeUse",
        };
        private readonly SourceText _original = SourceText.From(text);
        private readonly TextChange[] _changes = CSharpSyntaxTree.ParseText(text).GetRoot().DescendantTokens()
            .Where(token => token.IsKind(SyntaxKind.IdentifierToken) && Names.ContainsKey(token.ValueText))
            .Select(token => new TextChange(token.Span, Names[token.ValueText]))
            .Prepend(new TextChange(new TextSpan(0, 0), "global using Sunder.Sdk.Services;\n")).ToArray();

        internal string CurrentText => _original.WithChanges(_changes).ToString();

        internal SunderRpcCSharpSourceSpan OriginalSpan(SunderRpcCSharpSourceSpan span)
        {
            var current = _original.WithChanges(_changes);
            var start = Map(current.Lines.GetPosition(new(span.StartLine - 1, span.StartColumn - 1)));
            var end = Map(current.Lines.GetPosition(new(span.EndLine - 1, span.EndColumn - 1)));
            var from = _original.Lines.GetLinePosition(start); var to = _original.Lines.GetLinePosition(end);
            return new(span.Path, from.Line + 1, from.Character + 1, to.Line + 1, to.Character + 1);
        }

        private int Map(int position)
        {
            var delta = 0;
            foreach (var change in _changes)
            {
                var start = change.Span.Start + delta;
                if (position < start) break;
                if (position < start + change.NewText!.Length)
                    return change.Span.Start + Math.Min(position - start, change.Span.Length);
                delta += change.NewText!.Length - change.Span.Length;
            }
            return position - delta;
        }
    }
}
