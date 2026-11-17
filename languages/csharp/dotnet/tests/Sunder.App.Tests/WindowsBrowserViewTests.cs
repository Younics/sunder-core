using System.Runtime.Versioning;
using System.Text.Json;
using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.App.Tests;

public sealed class WindowsBrowserViewTests
{
    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void RetirementWaitsForItsOwnWorkerTargetsButNotSiblingOrNonWorkerTargets()
    {
        (string type, string? context, bool present)[] cases = [
            ("worker", "retiring", true), ("shared_worker", "retiring", true), ("service_worker", "retiring", true),
            ("service_worker", "sibling", false), ("page", "retiring", false), ("browser", null, false)];
        foreach (var (type, context, present) in cases)
        {
            using var targets = JsonDocument.Parse(JsonSerializer.Serialize(new { targetInfos = new[] { new { type, browserContextId = context } } }));
            Assert.Equal(present, WindowsBrowserView.HasWorkersInContext(targets.RootElement, "retiring"));
        }
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void UnscopedWorkersCannotSilentlyCountAsRetired()
    {
        foreach (var ownership in new[] { "{}", "{\"browserContextId\":null}", "{\"browserContextId\":\"\"}" })
        {
            using var owner = JsonDocument.Parse(ownership);
            var target = new Dictionary<string, object?> { ["type"] = "service_worker" };
            foreach (var property in owner.RootElement.EnumerateObject()) target[property.Name] = property.Value.Clone();
            using var targets = JsonDocument.Parse(JsonSerializer.Serialize(new { targetInfos = new[] { target } }));
            Assert.Throws<InvalidDataException>(() => WindowsBrowserView.HasWorkersInContext(targets.RootElement, "retiring"));
        }
    }
}
