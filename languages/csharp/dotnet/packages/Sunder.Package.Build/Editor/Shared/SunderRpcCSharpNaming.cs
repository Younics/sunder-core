using System;
using System.Collections.Generic;
using System.Linq;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

// The portable editor targets netstandard2.0 and does not reference SDK runtime
// assemblies. Keep this value type free of record/Index/Range runtime helpers.
internal sealed class SunderRpcCSharpName
{
    internal SunderRpcCSharpName(string lowerKebab, string contractSuffix, string pascal, string camel)
    { LowerKebab = lowerKebab; ContractSuffix = contractSuffix; Pascal = pascal; Camel = camel; }
    internal string LowerKebab { get; }
    internal string ContractSuffix { get; }
    internal string Pascal { get; }
    internal string Camel { get; }
}

internal static class SunderRpcCSharpNaming
{
    public static bool TryCreate(string sourceName, string? terminalSuffix, out SunderRpcCSharpName? name)
    {
        name = null;
        if (terminalSuffix is not null && sourceName.EndsWith(terminalSuffix, StringComparison.Ordinal))
        {
            sourceName = sourceName.Substring(0, sourceName.Length - terminalSuffix.Length);
        }
        if (sourceName.Length == 0 || sourceName[0] is '_' or '-' || sourceName[sourceName.Length - 1] is '_' or '-')
        {
            return false;
        }

        var tokens = new List<string>();
        var segmentStart = 0;
        for (var index = 0; index <= sourceName.Length; index++)
        {
            if (index == sourceName.Length || sourceName[index] is '_' or '-')
            {
                if (index == segmentStart || !TryTokenizeSegment(sourceName.Substring(segmentStart, index - segmentStart), tokens))
                {
                    return false;
                }
                segmentStart = index + 1;
                continue;
            }

            var character = sourceName[index];
            if (!IsAsciiLetter(character) && !IsAsciiDigit(character))
            {
                return false;
            }
        }

        var lower = tokens.Select(static token => token.ToLowerInvariant()).ToArray();
        var pascal = string.Concat(lower.Select(static token => char.ToUpperInvariant(token[0]) + token.Substring(1)));
        name = new SunderRpcCSharpName(
            string.Join("-", lower),
            string.Join(".", lower),
            pascal,
            lower[0] + string.Concat(lower.Skip(1).Select(static token => char.ToUpperInvariant(token[0]) + token.Substring(1))));
        return true;
    }

    private static bool TryTokenizeSegment(string segment, ICollection<string> tokens)
    {
        if (segment.Length == 0 || !IsAsciiLetter(segment[0]))
        {
            return false;
        }

        var tokenStart = 0;
        for (var index = 1; index < segment.Length; index++)
        {
            var character = segment[index];
            if (!IsAsciiLetter(character) && !IsAsciiDigit(character))
            {
                return false;
            }

            if (!IsAsciiUpper(character))
            {
                continue;
            }

            var previous = segment[index - 1];
            var nextIsLower = index + 1 < segment.Length && IsAsciiLower(segment[index + 1]);
            if (IsAsciiLower(previous) || IsAsciiDigit(previous) || IsAsciiUpper(previous) && nextIsLower)
            {
                tokens.Add(segment.Substring(tokenStart, index - tokenStart));
                tokenStart = index;
            }
        }

        tokens.Add(segment.Substring(tokenStart));
        return true;
    }

    private static bool IsAsciiLetter(char value) => IsAsciiLower(value) || IsAsciiUpper(value);

    private static bool IsAsciiLower(char value) => value is >= 'a' and <= 'z';

    private static bool IsAsciiUpper(char value) => value is >= 'A' and <= 'Z';

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
}
