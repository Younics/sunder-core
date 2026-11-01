using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;
using System.Text.Json;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class StackPreviewEndpointTests
{
    [Theory]
    [InlineData(nameof(RuntimeStackFailureCategory.PackageValidation))]
    [InlineData(nameof(RuntimeStackFailureCategory.Validation))]
    [InlineData(nameof(RuntimeStackFailureCategory.NotFound))]
    public async Task RejectedPreview_SerializesConflictsInputsAndWarningsAcrossHttpBoundary(string category)
    {
        var response = new RuntimeStackImportPreviewResponse(false, null, null, [],
            [new("input", "example.package", "contributor", "region", "Region", RuntimeStackInputSensitivity.Public, true)],
            [new("schema", "example.package", "contributor", "Unsupported schema version 2", "Error", "fragment")], ["Review warning"], []);
        var operation = RuntimeStackOperationResult<RuntimeStackImportPreviewResponse>.Failed(response, Enum.Parse<RuntimeStackFailureCategory>(category), null);
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        await StackEndpoints.PreviewResult(operation).ExecuteAsync(context);
        Assert.Equal(200, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Unsupported schema version 2", json.RootElement.GetProperty("conflicts")[0].GetProperty("message").GetString());
        Assert.Equal("Region", json.RootElement.GetProperty("requiredInputs")[0].GetProperty("label").GetString());
        Assert.Equal("Review warning", json.RootElement.GetProperty("warnings")[0].GetString());
    }

    [Theory]
    [InlineData(nameof(RuntimeStackFailureCategory.StaleGeneration), typeof(RuntimeStaleGenerationException))]
    [InlineData(nameof(RuntimeStackFailureCategory.Unavailable), typeof(RuntimeUnavailableException))]
    public void TransportFailures_AreNotDowngradedToSuccessfulHttpResponses(string category, Type exceptionType)
    {
        var response = new RuntimeStackImportPreviewResponse(false, null, null, [], [], [], [], ["Transport failure"]);
        var operation = RuntimeStackOperationResult<RuntimeStackImportPreviewResponse>.Failed(response, Enum.Parse<RuntimeStackFailureCategory>(category), "Transport failure");
        Assert.Throws(exceptionType, () => StackEndpoints.PreviewResult(operation));
    }
}
