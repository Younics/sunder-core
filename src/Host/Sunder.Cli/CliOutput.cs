using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sunder.Cli;

internal sealed partial class CliOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly bool _json;
    private readonly List<CliMessage> _messages = [];
    private object? _data;

    public CliOutput(TextWriter stdout, TextWriter stderr, bool json)
    {
        _stdout = stdout;
        _stderr = stderr;
        _json = json;
    }

    public void Line(string message = "")
    {
        if (_json) return;
        foreach (var line in message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
            _stdout.WriteLine(CliText.Sanitize(line));
    }

    public void Info(string message) => WriteMessage("info", message, error: false);
    public void Success(string message) => WriteMessage("success", message, error: false);
    public void Warning(string message) => WriteMessage("warning", message, error: true);
    public void Error(string message) => WriteMessage("error", message, error: true);

    public void Data(object? data) => _data = data;

    public void Complete(int exitCode)
    {
        if (!_json) return;
        var dataNode = _data is null ? null : JsonSerializer.SerializeToNode(_data, JsonOptions);
        SanitizeNode(dataNode);
        var envelope = new JsonObject
        {
            ["exitCode"] = exitCode,
            ["success"] = exitCode == CliExitCodes.Success,
            ["messages"] = new JsonArray(_messages.Select(message => new JsonObject
            {
                ["level"] = message.Level,
                ["message"] = message.Message,
            }).ToArray()),
            ["data"] = dataNode,
        };
        _stdout.WriteLine(envelope.ToJsonString(JsonOptions));
    }

    private void WriteMessage(string level, string message, bool error)
    {
        var safe = CliText.Sanitize(message);
        if (_json)
        {
            _messages.Add(new(level, safe));
            return;
        }
        (error ? _stderr : _stdout).WriteLine(safe);
    }

    private static void SanitizeNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (IsSecretProperty(property.Key)) obj[property.Key] = "[REDACTED]";
                else SanitizeNode(property.Value);
            }
            return;
        }
        if (node is JsonArray array)
        {
            foreach (var item in array) SanitizeNode(item);
            return;
        }
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            value.ReplaceWith(JsonValue.Create(CliText.Sanitize(text, 16 * 1024)));
        }
    }

    private static bool IsSecretProperty(string name)
        => name.Contains("token", StringComparison.OrdinalIgnoreCase)
           || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
           || name.Contains("password", StringComparison.OrdinalIgnoreCase);

    private sealed record CliMessage(string Level, string Message);
}

internal static partial class CliText
{
    [GeneratedRegex("(?is)<(?:!doctype|html|head|body|script|style|title|h[1-6]|div|p|span|form|pre)(?:\\s[^>]*)?>")]
    private static partial Regex HtmlRegex();

    [GeneratedRegex("(?i)(bearer\\s+|(?:access[_-]?token|refresh[_-]?token|token|secret|password)\\s*[:=]\\s*)[^\\s,;]+")]
    private static partial Regex SecretRegex();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex JwtRegex();

    public static string Sanitize(string? value, int maxLength = 1000)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var text = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        text = new string(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        if (HtmlRegex().IsMatch(text)) text = "The server returned an untrusted HTML response.";
        text = SecretRegex().Replace(text, match => $"{match.Groups[1].Value}[REDACTED]");
        text = JwtRegex().Replace(text, "[REDACTED]");
        return text.Length <= maxLength ? text.Trim() : $"{text[..maxLength].Trim()}...";
    }
}

internal interface ICliProgress
{
    void Report(string message);
}

internal sealed class CliProgress(CliOutput output) : ICliProgress
{
    public void Report(string message) => output.Info(message);
}
