using System.Text;
using System.Text.RegularExpressions;

namespace Sunder.Package.Format;

internal static class SunderStackSecretScanner
{
    private const long MaxScannedFileBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Regex SensitiveJsonStringRegex = new(
        "\"(?<name>password|passwd|apiKey|api_key|api-key|secret|secretKey|secret_key|secret-key|accessToken|access_token|access-token|refreshToken|refresh_token|refresh-token|clientSecret|client_secret|client-secret|authorization)\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"\\\\]){12,})\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly SecretPattern[] SecretPatterns =
    [
        new("private key", new Regex("-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)),
        new("Anthropic API key", new Regex("\\bsk-ant-[A-Za-z0-9_-]{32,}\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        new("OpenAI API key", new Regex("\\bsk-(?!ant-)(?:proj-|svcacct-)?[A-Za-z0-9_-]{32,}\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        new("GitHub token", new Regex("\\bgh[pousr]_[A-Za-z0-9_]{36,255}\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        new("Google API key", new Regex("\\bAIza[0-9A-Za-z_-]{35}\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        new("Slack token", new Regex("\\bxox[baprs]-[A-Za-z0-9-]{20,}\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        new("Azure storage account key", new Regex("\\bAccountKey=[A-Za-z0-9+/=]{40,}", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)),
    ];

    public static IReadOnlyList<string> ScanExtractedStack(string stagingPath)
    {
        var findings = new List<string>();
        foreach (var file in SunderArchive.EnumerateFiles(stagingPath))
        {
            var relativePath = file.Path.ToString();
            var text = TryReadUtf8Text(file.FullPath);
            if (text is null)
            {
                continue;
            }

            ScanText(relativePath, text, findings);
        }

        return findings;
    }

    private static void ScanText(string relativePath, string text, ICollection<string> findings)
    {
        foreach (var pattern in SecretPatterns)
        {
            var match = pattern.Regex.Match(text);
            if (match.Success)
            {
                findings.Add(BuildFinding(relativePath, pattern.Label, text, match.Index));
            }
        }

        foreach (Match match in SensitiveJsonStringRegex.Matches(text))
        {
            var value = match.Groups["value"].Value;
            if (IsPlaceholderValue(value))
            {
                continue;
            }

            findings.Add(BuildFinding(relativePath, $"raw {match.Groups["name"].Value} value", text, match.Index));
        }
    }

    private static string BuildFinding(string relativePath, string label, string text, int matchIndex)
        => $"Stack file '{relativePath}' appears to contain {label} material at line {GetLineNumber(text, matchIndex)}. Raw secrets are not allowed in .sunderstack archives.";

    private static int GetLineNumber(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string? TryReadUtf8Text(string filePath)
    {
        var info = new FileInfo(filePath);
        if (info.Length > MaxScannedFileBytes)
        {
            return null;
        }

        var bytes = File.ReadAllBytes(filePath);
        if (bytes.Contains((byte)0))
        {
            return null;
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool IsPlaceholderValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length < 12)
        {
            return true;
        }

        if (trimmed.All(static character => character == '*'))
        {
            return true;
        }

        if (trimmed.StartsWith('$')
            || trimmed.StartsWith("{{", StringComparison.Ordinal)
            || (trimmed.StartsWith('<') && trimmed.EndsWith('>'))
            || trimmed.StartsWith("sunder:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("secret://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return trimmed.Contains("redacted", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains("required at import", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains("provided by user", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SecretPattern(string Label, Regex Regex);
}
