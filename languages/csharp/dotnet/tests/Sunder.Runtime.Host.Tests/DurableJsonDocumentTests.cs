using System.Text.Json;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class DurableJsonDocumentTests
{
    [WindowsFact]
    public async Task DurableReplacementsAndDirectoryMovesSupportPathsBeyondMaxPath()
    {
        await using var temporary = TemporaryDirectoryScope.Create("sunder-durable-document");
        var root = temporary.RootPath;
        while (root.Length < 350) root = Path.Combine(root, new string('p', 60));
        var path = Path.Combine(root, "receipt.json");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        await DurableJsonDocument.WriteAsync(path, new { revision = 1 }, options);
        await DurableJsonDocument.WriteAsync(path, new { revision = 2 }, options);
        using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path)))
            Assert.Equal(2, document.RootElement.GetProperty("revision").GetInt32());
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp-*"));

        var staged = Path.Combine(root, "candidate");
        var committed = Path.Combine(root, "committed");
        Directory.CreateDirectory(staged);
        await File.WriteAllTextAsync(Path.Combine(staged, "payload"), "owned");
        DurableJsonDocument.MoveDirectory(staged, committed);
        Assert.False(Directory.Exists(staged));
        Assert.Equal("owned", await File.ReadAllTextAsync(Path.Combine(committed, "payload")));
    }
}
