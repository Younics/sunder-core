using System.Text.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeRequiredProviderPlanTests
{
    [Fact]
    public void CycleDiagnosticIdentifiesTheCycleWithoutItsDownstreamConsumers()
    {
        var plan = new RuntimeRequiredProviderPlan(new Dictionary<string, IReadOnlyList<string>>
        { ["example.first"] = [], ["example.second"] = [], ["example.downstream"] = [] },
        [new(Predicate("example.first"), ["example.second"]), new(Predicate("example.second"), ["example.first"]), new(Predicate("example.downstream"), ["example.first"])]);
        var failure = Assert.Throws<RuntimeRequiredProviderException>(plan.Validate);
        Assert.Equal(RpcReadinessErrorCodes.RequiredProviderCycle, failure.Code);
        Assert.Equal(new[] { "example.first", "example.second" }, failure.PackageIds);
        Assert.All(failure.Predicates, predicate => Assert.Contains(predicate.ConsumerPackageId, failure.PackageIds));
        Assert.IsType<RuntimeRequiredProviderCycleDetails>(failure.Details);
    }

    [Fact]
    public void DiagnosticsAreDetachedSortedAndFitTheClientErrorBudget()
    {
        var source = Enumerable.Range(0, 400).Reverse().Select(index => Predicate("example.consumer" + index.ToString("D4"))).ToList();
        var failure = new RuntimeRequiredProviderException(RpcReadinessErrorCodes.RequiredProviderUnsatisfied,
            source.Select(predicate => predicate.ConsumerPackageId).ToArray(), source);
        source.Clear();
        Assert.InRange(failure.Predicates.Count, 1, 128);
        Assert.Equal(failure.Predicates.OrderBy(predicate => predicate.ConsumerPackageId, StringComparer.Ordinal), failure.Predicates);
        Assert.All(failure.Predicates, predicate => Assert.Equal(new[] { "discover", "invoke" }, predicate.Actions));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(failure.Details, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length <= 48 * 1024);
        Assert.DoesNotContain("descriptorSha256", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    internal static RuntimeRequiredProviderPredicate Predicate(string packageId) => new(packageId, "runtime",
        new("example.required", "1.0.0", new('a', 64), new('b', 64)), ["invoke", "discover"]);
}
