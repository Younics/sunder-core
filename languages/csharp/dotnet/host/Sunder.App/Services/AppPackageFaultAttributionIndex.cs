using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Sunder.App.Services;

internal sealed record AppPackageFaultAttribution(
    Guid GenerationId,
    string PackageId,
    string? ContentHash);

internal sealed class AppPackageFaultAttributionIndex
{
    private readonly ConditionalWeakTable<Assembly, RegistrationSet> _registrations = new();

    public void Register(
        Guid generationId,
        string packageId,
        string? contentHash,
        Assembly assembly)
        => _registrations
            .GetValue(assembly, static _ => new RegistrationSet())
            .Add(new AppPackageFaultAttribution(
                generationId,
                packageId,
                contentHash));

    public void Unregister(
        Guid generationId,
        IReadOnlyList<(string PackageId, Assembly Assembly)> registrations)
    {
        foreach (var assembly in registrations
                     .Select(static registration => registration.Assembly)
                     .Distinct())
        {
            if (_registrations.TryGetValue(assembly, out var registered))
            {
                registered.Remove(generationId);
            }
        }
    }

    public AppPackageFaultAttribution? ResolveExclusive(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var attributions = new List<AppPackageFaultAttribution>();
        var pending = new Stack<(Exception Exception, bool PathAttributed)>();
        pending.Push((exception, false));
        while (pending.TryPop(out var item))
        {
            var currentAttributed = CollectAttributions(item.Exception, attributions);
            if (!HasSingleIdentity(attributions))
            {
                return null;
            }

            var pathAttributed = item.PathAttributed || currentAttributed;
            if (item.Exception is AggregateException aggregate)
            {
                if (aggregate.InnerExceptions.Count == 0 && !pathAttributed)
                {
                    return null;
                }
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push((inner, pathAttributed));
                }
            }
            else if (item.Exception.InnerException is { } inner)
            {
                pending.Push((inner, pathAttributed));
            }
            else if (!pathAttributed)
            {
                return null;
            }
        }

        if (attributions.Count == 0 || !HasSingleIdentity(attributions))
        {
            return null;
        }

        var first = attributions[0];
        var generationIds = attributions
            .Select(static attribution => attribution.GenerationId)
            .Distinct()
            .ToArray();
        if (string.IsNullOrWhiteSpace(first.ContentHash)
            && generationIds.Length != 1)
        {
            return null;
        }

        return first with
        {
            GenerationId = generationIds.Length == 1 ? generationIds[0] : Guid.Empty,
        };
    }

    private bool CollectAttributions(
        Exception exception,
        ICollection<AppPackageFaultAttribution> attributions)
    {
        var attributed = false;
        var assembly = exception.TargetSite?.DeclaringType?.Assembly;
        if (assembly is not null)
        {
            attributed |= AddRegistrations(assembly, attributions);
        }

        foreach (var frame in new StackTrace(exception, fNeedFileInfo: false).GetFrames() ?? [])
        {
            assembly = frame.GetMethod()?.DeclaringType?.Assembly;
            if (assembly is not null)
            {
                attributed |= AddRegistrations(assembly, attributions);
            }
        }
        return attributed;
    }

    private bool AddRegistrations(
        Assembly assembly,
        ICollection<AppPackageFaultAttribution> attributions)
    {
        if (!_registrations.TryGetValue(assembly, out var registrations))
        {
            return false;
        }

        foreach (var attribution in registrations.Snapshot())
        {
            attributions.Add(attribution);
        }
        return true;
    }

    private static bool HasSingleIdentity(
        IReadOnlyCollection<AppPackageFaultAttribution> attributions)
    {
        if (attributions.Count <= 1)
        {
            return true;
        }

        var first = attributions.First();
        return attributions.All(attribution =>
            string.Equals(
                attribution.PackageId,
                first.PackageId,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                attribution.ContentHash,
                first.ContentHash,
                StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RegistrationSet
    {
        private readonly object _syncRoot = new();
        private readonly List<AppPackageFaultAttribution> _items = [];

        public void Add(AppPackageFaultAttribution attribution)
        {
            lock (_syncRoot)
            {
                if (!_items.Contains(attribution))
                {
                    _items.Add(attribution);
                }
            }
        }

        public AppPackageFaultAttribution[] Snapshot()
        {
            lock (_syncRoot)
            {
                return _items.ToArray();
            }
        }

        public void Remove(Guid generationId)
        {
            lock (_syncRoot)
            {
                _items.RemoveAll(attribution =>
                    attribution.GenerationId == generationId);
            }
        }
    }
}
