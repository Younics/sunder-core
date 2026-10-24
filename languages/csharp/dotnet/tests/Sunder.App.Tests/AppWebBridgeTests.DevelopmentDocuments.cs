using System.Text.Json;
using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed partial class AppWebBridgeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevelopmentDocumentAdapterRequiresTrustedLifetimeAndEmbedsOnlyFixedCommandsAndJsonData(bool jsonStringResult)
    {
        using var temporary = TemporaryDirectoryScope.Create("development-document-adapter");
        await File.WriteAllTextAsync(temporary.GetPath("index.html"), "<!doctype html><html><body></body></html>");
        await using var server = await AppWebContentServer.StartAsync(temporary.RootPath, Target(), default);
        using var web = new TestWebView();
        await using var bridge = new AppWebBridge(web, server, UnavailableAppWebRpcClient.Instance, EmptyCatalog(temporary.RootPath), new(), _ => { });
        var adapter = new AppWebDevelopmentDocument(web, bridge);
        var snapshotId = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.InspectAsync(snapshotId, default));
        Assert.Empty(web.Scripts);
        await bridge.EstablishAsync(server.BaseUri);
        web.ClearScripts();
        var reference = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new { state = "inspected", elements = new[] { new { reference, role = "textbox", name = "Input label", isEnabled = true, actions = new[] { "set-text" } } }, wasTruncated = false });
        web.EvaluateScript = _ => Task.FromResult<string?>(jsonStringResult ? JsonSerializer.Serialize(payload) : payload);
        var snapshot = await adapter.InspectAsync(snapshotId, default);
        Assert.Equal(bridge.SessionNonce, snapshot.DocumentId);
        Assert.Single(snapshot.Elements);
        var inspectRequest = ReadDevelopmentScriptRequest(Assert.Single(web.Scripts));
        Assert.Equal("inspect", inspectRequest.GetProperty("command").GetString());
        Assert.Equal(snapshotId, inspectRequest.GetProperty("snapshotId").GetString());
        web.ClearScripts();
        web.EvaluateScript = _ => Task.FromResult<string?>("{\"state\":\"dispatched\"}");
        const string input = "'); globalThis.arbitraryScript = true; // \n😀";
        Assert.True(await adapter.InteractAsync(snapshot.DocumentId, new(Guid.NewGuid().ToString("N"), snapshotId, reference, "set-text", input), reference, default));
        var inputScript = Assert.Single(web.Scripts);
        var inputRequest = ReadDevelopmentScriptRequest(inputScript);
        Assert.Equal(input, inputRequest.GetProperty("text").GetString());
        Assert.Equal("input", inputRequest.GetProperty("command").GetString());
        Assert.DoesNotContain(input, inputScript, StringComparison.Ordinal);
        await bridge.NavigationStartingAsync();
        web.ClearScripts();
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.InteractAsync(snapshot.DocumentId,
            new(Guid.NewGuid().ToString("N"), snapshotId, reference, "set-text", "replacement"), reference, default));
        Assert.Empty(web.Scripts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevelopmentDocumentInspectionDoesNotReturnDataFromANavigatedOrCancelledNativeEvaluation(bool cancel)
    {
        using var temporary = TemporaryDirectoryScope.Create("development-document-navigation");
        await File.WriteAllTextAsync(temporary.GetPath("index.html"), "<!doctype html><html><body></body></html>");
        await using var server = await AppWebContentServer.StartAsync(temporary.RootPath, Target(), default);
        using var web = new TestWebView();
        await using var bridge = new AppWebBridge(web, server, UnavailableAppWebRpcClient.Instance, EmptyCatalog(temporary.RootPath), new(), _ => { });
        await bridge.EstablishAsync(server.BaseUri);
        var adapter = new AppWebDevelopmentDocument(web, bridge);
        var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        web.EvaluateScript = script => script.Contains("__sunder_development_", StringComparison.Ordinal) ? reply.Task : Task.FromResult<string?>("true");
        using var caller = new CancellationTokenSource();
        var inspection = adapter.InspectAsync(Guid.NewGuid().ToString("N"), caller.Token);
        Assert.False(inspection.IsCompleted);
        if (cancel) caller.Cancel(); else await bridge.NavigationStartingAsync();
        Assert.False(inspection.IsCompleted); // Retirement does not abandon outstanding native evaluation.
        reply.SetResult("{\"state\":\"inspected\",\"elements\":[],\"wasTruncated\":false}");
        if (cancel)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspection);
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => inspection);
    }

    private static JsonElement ReadDevelopmentScriptRequest(string script)
    {
        var offset = script.LastIndexOf(")(", StringComparison.Ordinal) + 2;
        using var json = JsonDocument.Parse(script[offset..^1]);
        return json.RootElement.Clone();
    }
}
