using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record RequiredProviderPredicate(RuntimeRequiredProviderPredicate Details, IReadOnlyList<string> Alternatives);

internal sealed class RuntimeRequiredProviderException : RuntimeException
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal RuntimeRequiredProviderException(string code, IReadOnlyList<string> packageIds, IReadOnlyList<RuntimeRequiredProviderPredicate> predicates)
        : base(code, "Required provider graph is not ready", StatusCodes.Status409Conflict, MessageFor(code))
    {
        var ids = packageIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (code == RpcReadinessErrorCodes.RequiredProviderCycle && ids.Length > 128)
            throw new RuntimeValidationException("The required-provider cycle exceeds the supported diagnostic bound.");
        var details = predicates.Select(predicate => predicate with { Actions = predicate.Actions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() })
            .OrderBy(PredicateKey, StringComparer.Ordinal).DistinctBy(PredicateKey, StringComparer.Ordinal).Take(128).ToList();
        if (details.Count == 0) throw new RuntimeValidationException("The required-provider failure has no diagnostic predicates.");
        while (true)
        {
            PackageIds = Array.AsReadOnly(code == RpcReadinessErrorCodes.RequiredProviderCycle ? ids
                : details.Select(predicate => predicate.ConsumerPackageId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
            Predicates = Array.AsReadOnly(details.ToArray());
            Details = code == RpcReadinessErrorCodes.RequiredProviderCycle
                ? new RuntimeRequiredProviderCycleDetails(PackageIds, Predicates) : new RuntimeRequiredProviderUnsatisfiedDetails(Predicates);
            if (JsonSerializer.SerializeToUtf8Bytes(Details, Json).Length <= 48 * 1024) break;
            if (details.Count == 1) throw new RuntimeValidationException("The required-provider diagnostic exceeds its byte bound.");
            details.RemoveAt(details.Count - 1);
        }
    }

    internal IReadOnlyList<string> PackageIds { get; }
    internal IReadOnlyList<RuntimeRequiredProviderPredicate> Predicates { get; }
    public override object Details { get; }
    private static string PredicateKey(RuntimeRequiredProviderPredicate predicate) => string.Join('\0', predicate.ConsumerPackageId,
        predicate.ConsumerRole, predicate.Contract.ContractId, predicate.Contract.ContractVersion, predicate.Contract.DescriptorSha256,
        predicate.Contract.AbiSha256, string.Join('\0', predicate.Actions));
    private static string MessageFor(string code) => code is RpcReadinessErrorCodes.RequiredProviderUnsatisfied or RpcReadinessErrorCodes.RequiredProviderCycle
        ? PolicyErrorCatalog.All.Single(error => error.Code == code).SafeMessage
        : throw new ArgumentException("Unknown required-provider error code.", nameof(code));
}

internal sealed class RuntimeRequiredProviderPlan(
    IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies,
    IReadOnlyList<RequiredProviderPredicate> predicates)
{
    internal IReadOnlyList<RequiredProviderPredicate> Predicates => predicates;

    internal bool CanStart(string packageId, IReadOnlySet<string> ready) => dependencies[packageId].All(ready.Contains)
        && predicates.Where(value => value.Details.ConsumerRole == SunderPackageFormat.RuntimeHostRole && value.Details.ConsumerPackageId == packageId)
            .All(value => value.Alternatives.Any(provider => provider == packageId || ready.Contains(provider)));

    internal void Validate()
    {
        var missing = predicates.Where(value => value.Alternatives.Count == 0).ToArray();
        if (missing.Length > 0) throw Failure(RpcReadinessErrorCodes.RequiredProviderUnsatisfied, missing);
        var ready = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var frontier = dependencies.Keys.Where(id => !ready.Contains(id) && CanStart(id, ready)).Order(StringComparer.Ordinal).ToArray();
            if (frontier.Length == 0) break;
            ready.UnionWith(frontier);
        }
        var stalled = dependencies.Keys.Where(id => !ready.Contains(id)).Order(StringComparer.Ordinal).ToArray();
        if (stalled.Length > 0)
        {
            var cycle = FindCycle(stalled, ready);
            throw new RuntimeRequiredProviderException(RpcReadinessErrorCodes.RequiredProviderCycle, cycle,
                predicates.Where(value => cycle.Contains(value.Details.ConsumerPackageId, StringComparer.Ordinal)).Select(value => value.Details).ToArray());
        }
    }

    internal RuntimeRequiredProviderException Stalled(IEnumerable<string> packages)
    {
        var ids = packages.Order(StringComparer.Ordinal).ToArray();
        return new(RpcReadinessErrorCodes.RequiredProviderUnsatisfied, ids,
            predicates.Where(value => ids.Contains(value.Details.ConsumerPackageId, StringComparer.Ordinal)).Select(value => value.Details).ToArray());
    }

    private static RuntimeRequiredProviderException Failure(string code, IReadOnlyList<RequiredProviderPredicate> values) => new(code,
        values.Select(value => value.Details.ConsumerPackageId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        values.Select(value => value.Details).ToArray());

    private IReadOnlyList<string> FindCycle(IReadOnlyList<string> stalled, IReadOnlySet<string> ready)
    {
        var members = stalled.ToHashSet(StringComparer.Ordinal);
        var edges = stalled.ToDictionary(id => id, id => dependencies[id]
            .Concat(predicates.Where(predicate => predicate.Details.ConsumerRole == SunderPackageFormat.RuntimeHostRole
                && predicate.Details.ConsumerPackageId == id && !predicate.Alternatives.Any(provider => provider == id || ready.Contains(provider)))
                .SelectMany(predicate => predicate.Alternatives))
            .Where(members.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var reverse = stalled.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (source, targets) in edges) foreach (var target in targets) reverse[target].Add(source);

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var finished = new List<string>();
        foreach (var root in stalled)
        {
            var pending = new Stack<(string Id, bool Expanded)>();
            pending.Push((root, false));
            while (pending.TryPop(out var item))
            {
                if (item.Expanded) { finished.Add(item.Id); continue; }
                if (!visited.Add(item.Id)) continue;
                pending.Push((item.Id, true));
                foreach (var target in edges[item.Id].Reverse()) pending.Push((target, false));
            }
        }
        visited.Clear();
        var cycles = new List<string[]>();
        foreach (var root in finished.AsEnumerable().Reverse())
        {
            if (visited.Contains(root)) continue;
            var component = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var id))
            {
                if (!visited.Add(id)) continue;
                component.Add(id);
                foreach (var source in reverse[id]) pending.Push(source);
            }
            if (component.Count > 1 || edges[root].Contains(root, StringComparer.Ordinal)) cycles.Add(component.Order(StringComparer.Ordinal).ToArray());
        }
        return cycles.OrderBy(component => component[0], StringComparer.Ordinal).FirstOrDefault()
            ?? throw new RuntimeValidationException("The requested graph contains an unavailable normal package dependency.");
    }
}
