using System.Text.Json;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Format;

internal enum SunderRpcContractChangeKind
{
    Unchanged,
    Patch,
    Minor,
    Major,
}

internal sealed record SunderRpcContractChange(string Path, string Code);

internal sealed record SunderRpcContractCompatibilityResult(
    SunderRpcContractChangeKind Kind,
    IReadOnlyList<SunderRpcContractChange> Changes);

// Admission/authority and descriptor validation belong to callers. This is a bounded proof
// over the parser's normalized V1 ABI, not a second descriptor or JSON Schema parser.
internal static class SunderRpcContractCompatibility
{
    private const string PrereleaseDiagnostic =
        "contract-version.prerelease-policy-required: Reuse an exact immutable version or freeze an explicit prerelease transition policy before allocating a new version.";

    internal static SunderRpcContractCompatibilityResult Classify(
        SunderRpcContractDescriptor previous,
        SunderRpcContractDescriptor candidate)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(candidate);
        if (previous.GetCanonicalUtf8().AsSpan().SequenceEqual(candidate.GetCanonicalUtf8()))
            return new(SunderRpcContractChangeKind.Unchanged, Array.Empty<SunderRpcContractChange>());

        var oldAbi = previous.GetCanonicalAbiProjectionUtf8();
        var newAbi = candidate.GetCanonicalAbiProjectionUtf8();
        if (oldAbi.AsSpan().SequenceEqual(newAbi))
            return new(SunderRpcContractChangeKind.Patch, [new("", "abi-equivalent")]);

        using var oldDocument = JsonDocument.Parse(oldAbi, new JsonDocumentOptions { MaxDepth = 256 });
        using var newDocument = JsonDocument.Parse(newAbi, new JsonDocumentOptions { MaxDepth = 256 });
        var proof = new Proof(oldDocument.RootElement, newDocument.RootElement);
        try
        {
            proof.Compare();
        }
        catch (ProofLimitException)
        {
            proof.Change(SunderRpcContractChangeKind.Major, "", "analysis-limit-exceeded");
        }
        return proof.Result();
    }

    internal static string GetMinimumVersion(string previousVersion, SunderRpcContractChangeKind kind)
    {
        if (!SemanticVersion.TryParse(previousVersion, out var previous))
            throw new ArgumentException("A valid strict descriptor SemVer is required.", nameof(previousVersion));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == SunderRpcContractChangeKind.Unchanged) return previousVersion;
        if (previous.Prerelease is not null) throw new InvalidOperationException(PrereleaseDiagnostic);
        var minimum = MinimumStableVersion(previous, kind);
        if (minimum.Length > SemanticVersion.MaximumLength)
            throw new InvalidOperationException(
                "contract-version.version-limit-exceeded: The minimum increment exceeds the descriptor version length limit; select a higher stable bump that fits the limit.");
        return minimum;
    }

    internal static IReadOnlyList<string> ValidateVersion(
        SunderRpcContractDescriptor candidate,
        IReadOnlyList<SunderRpcContractDescriptor> immutableHistory)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(immutableHistory);
        var candidateBytes = candidate.GetCanonicalUtf8();
        var reused = false;
        SunderRpcContractDescriptor? highest = null;
        var highestVersion = default(SemanticVersion);
        foreach (var previous in immutableHistory)
        {
            ArgumentNullException.ThrowIfNull(previous);
            if (previous.ContractId != candidate.ContractId)
                return ["contract-version.bump-required: Immutable history must contain only the candidate contract family."];
            if (previous.Version == candidate.Version)
            {
                if (!candidateBytes.AsSpan().SequenceEqual(previous.GetCanonicalUtf8()))
                    return [$"contract-version.bump-required: Candidate '{candidate.Version}' changes immutable canonical bytes at the same version; allocate a new version."];
                reused = true;
            }
            var version = SemanticVersion.Parse(previous.Version);
            if (highest is null || version.CompareTo(highestVersion) > 0)
            {
                highest = previous;
                highestVersion = version;
            }
        }

        // Reusing any exact immutable tuple is not a new allocation, including old majors
        // and prereleases. Conflicting duplicates above still fail closed.
        if (reused) return Array.Empty<string>();
        var candidateVersion = SemanticVersion.Parse(candidate.Version);
        if (candidateVersion.Prerelease is not null) return [PrereleaseDiagnostic];
        if (highest is null) return Array.Empty<string>();
        if (highestVersion.Prerelease is not null) return [PrereleaseDiagnostic];
        if (candidateVersion.ComparePrecedenceTo(highestVersion) <= 0)
            return [$"contract-version.bump-required: Candidate '{candidate.Version}' must have greater SemVer precedence than highest immutable version '{highest.Version}'; backfills and build-metadata-only allocations are forbidden."];

        string? failure = null;
        string? strictestMinimum = null;
        string? strictestPrior = null;
        foreach (var previous in immutableHistory)
        {
            var version = SemanticVersion.Parse(previous.Version);
            if (version.Major != candidateVersion.Major && previous.Version != highest.Version) continue;
            if (version.Prerelease is not null) return [PrereleaseDiagnostic];
            var kind = Classify(previous, candidate).Kind;
            var minimum = MinimumStableVersion(version, kind);
            if (CompareStableCore(candidateVersion, minimum) >= 0) continue;
            // Each historical minimum is anchored to its own version, not repeatedly
            // applied to the highest version (which would turn every patch into a minor).
            if (strictestMinimum is null
                || CompareStableText(minimum, strictestMinimum) > 0
                || CompareStableText(minimum, strictestMinimum) == 0
                && string.CompareOrdinal(previous.Version, strictestPrior) < 0)
            {
                strictestMinimum = minimum;
                strictestPrior = previous.Version;
                failure = $"contract-version.bump-required: Candidate '{candidate.Version}' requires {kind} relative to immutable version '{previous.Version}'.";
            }
        }
        return failure is null ? Array.Empty<string>() : [failure];
    }

    private static string MinimumStableVersion(SemanticVersion previous, SunderRpcContractChangeKind kind)
        => kind switch
        {
            SunderRpcContractChangeKind.Unchanged => $"{previous.Major}.{previous.Minor}.{previous.Patch}",
            SunderRpcContractChangeKind.Patch => $"{previous.Major}.{previous.Minor}.{Increment(previous.Patch)}",
            SunderRpcContractChangeKind.Minor => $"{previous.Major}.{Increment(previous.Minor)}.0",
            SunderRpcContractChangeKind.Major => $"{Increment(previous.Major)}.0.0",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string Increment(string digits)
    {
        var result = digits.ToCharArray();
        for (var index = result.Length - 1; index >= 0; index--)
        {
            if (result[index] != '9')
            {
                result[index]++;
                return new string(result);
            }
            result[index] = '0';
        }
        return "1" + new string(result);
    }

    private static int CompareStableCore(SemanticVersion version, string minimum)
        => CompareStableText($"{version.Major}.{version.Minor}.{version.Patch}", minimum);

    private static int CompareStableText(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < 3; index++)
        {
            var order = leftParts[index].Length.CompareTo(rightParts[index].Length);
            if (order == 0) order = string.CompareOrdinal(leftParts[index], rightParts[index]);
            if (order != 0) return order;
        }
        return 0;
    }

    private sealed class ProofLimitException : Exception { }

    private sealed class Proof(JsonElement previous, JsonElement candidate)
    {
        private const int MaximumSteps = 65_536;
        private const int MaximumChanges = 64;
        private readonly JsonElement _oldDefinitions = previous.GetProperty("$defs");
        private readonly JsonElement _newDefinitions = candidate.GetProperty("$defs");
        private readonly Dictionary<string, string[]> _oldReferences = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> _newReferences = new(StringComparer.Ordinal);
        private readonly HashSet<string> _equalDefinitions = new(StringComparer.Ordinal);
        private readonly HashSet<string> _examinedDefinitions = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Name, bool Request), bool> _provedDefinitions = new();
        private readonly List<SunderRpcContractChange> _changes = [];
        private SunderRpcContractChangeKind _kind = SunderRpcContractChangeKind.Minor;
        private int _steps;

        internal SunderRpcContractCompatibilityResult Result()
            => new(_kind, Array.AsReadOnly(_changes
                .OrderBy(static change => change.Path, StringComparer.Ordinal)
                .ThenBy(static change => change.Code, StringComparer.Ordinal).ToArray()));

        internal void Change(SunderRpcContractChangeKind kind, string path, string code)
        {
            if (kind > _kind) _kind = kind;
            var change = new SunderRpcContractChange(path, code);
            if (_changes.Contains(change)) return;
            if (_changes.Count < MaximumChanges - 1) _changes.Add(change);
            else if (_changes.Count == MaximumChanges - 1) _changes.Add(new("", "changes-truncated"));
        }

        private void Step(int depth = 0)
        {
            if (++_steps > MaximumSteps || depth > 128) throw new ProofLimitException();
        }

        private static string At(string path, string member)
        {
            var result = path + "/" + member.Replace("~", "~0", StringComparison.Ordinal)
                .Replace("/", "~1", StringComparison.Ordinal);
            if (result.Length > 1024) throw new ProofLimitException();
            return result;
        }

        private bool Fail(string path, string code)
        {
            Change(SunderRpcContractChangeKind.Major, path, code);
            return false;
        }

        internal void Compare()
        {
            if (previous.GetProperty("contractId").GetString() != candidate.GetProperty("contractId").GetString())
                Fail("/contractId", "contract-id-changed");
            BuildNamedGraph();
            CompareServices();
            CompareErrors();
            foreach (var definition in _oldDefinitions.EnumerateObject())
            {
                Step();
                var path = At("/$defs", definition.Name);
                if (!_newDefinitions.TryGetProperty(definition.Name, out _))
                    Fail(path, "definition-removed");
                else if (!_equalDefinitions.Contains(definition.Name) && !_examinedDefinitions.Contains(definition.Name))
                    Fail(path, "definition-change-unclassified");
            }
            foreach (var definition in _newDefinitions.EnumerateObject())
            {
                Step();
                if (!_oldDefinitions.TryGetProperty(definition.Name, out _))
                    Change(SunderRpcContractChangeKind.Minor, At("/$defs", definition.Name), "definition-added");
            }
        }

        private void BuildNamedGraph()
        {
            var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var definition in _oldDefinitions.EnumerateObject())
            {
                Step();
                var references = References(definition.Value).Distinct(StringComparer.Ordinal).ToArray();
                _oldReferences.Add(definition.Name, references);
                foreach (var reference in references)
                {
                    if (!dependents.TryGetValue(reference, out var names)) dependents.Add(reference, names = []);
                    names.Add(definition.Name);
                }
                if (_newDefinitions.TryGetProperty(definition.Name, out var other) && Same(definition.Value, other))
                    _equalDefinitions.Add(definition.Name);
            }
            foreach (var definition in _newDefinitions.EnumerateObject())
                _newReferences.Add(definition.Name, References(definition.Value).Distinct(StringComparer.Ordinal).ToArray());

            // Exact local equality alone is insufficient: an unchanged $ref can point at
            // a changed definition. Propagate inequality through the finite named graph.
            var changed = new Queue<string>(_oldReferences.Keys.Where(name => !_equalDefinitions.Contains(name)));
            while (changed.TryDequeue(out var name))
            {
                Step();
                if (!dependents.TryGetValue(name, out var names)) continue;
                foreach (var dependent in names)
                    if (_equalDefinitions.Remove(dependent)) changed.Enqueue(dependent);
            }
        }

        private IEnumerable<string> References(JsonElement schema, int depth = 0)
        {
            Step(depth);
            if (schema.TryGetProperty("$ref", out var reference))
            {
                yield return reference.GetString()!["#/$defs/".Length..];
                yield break;
            }
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var property in properties.EnumerateObject())
                    foreach (var name in References(property.Value, depth + 1)) yield return name;
            foreach (var keyword in new[] { "items", "additionalProperties", "propertyNames" })
                if (schema.TryGetProperty(keyword, out var child) && child.ValueKind == JsonValueKind.Object)
                    foreach (var name in References(child, depth + 1)) yield return name;
            if (schema.TryGetProperty("oneOf", out var variants))
                foreach (var variant in variants.EnumerateArray())
                    foreach (var name in References(variant, depth + 1)) yield return name;
        }

        private bool HasCycle(string name, Dictionary<string, string[]> graph, HashSet<string> active, HashSet<string> complete)
        {
            Step(active.Count);
            if (complete.Contains(name)) return false;
            if (!active.Add(name)) return true;
            foreach (var reference in graph[name])
                if (HasCycle(reference, graph, active, complete)) return true;
            active.Remove(name);
            complete.Add(name);
            return false;
        }

        private void CompareServices()
        {
            var oldServices = previous.GetProperty("services");
            var newServices = candidate.GetProperty("services");
            foreach (var service in oldServices.EnumerateObject())
            {
                Step();
                var servicePath = At("/services", service.Name);
                if (!newServices.TryGetProperty(service.Name, out var otherService))
                {
                    Fail(servicePath, "service-removed");
                    continue;
                }
                var oldMethods = service.Value.GetProperty("methods");
                var newMethods = otherService.GetProperty("methods");
                foreach (var method in oldMethods.EnumerateObject())
                {
                    Step();
                    var path = At(servicePath + "/methods", method.Name);
                    if (!newMethods.TryGetProperty(method.Name, out var other))
                    {
                        Fail(path, "method-removed");
                        continue;
                    }
                    var oldKind = method.Value.GetProperty("kind").GetString();
                    var newKind = other.GetProperty("kind").GetString();
                    if (oldKind != newKind) Fail(path + "/kind", "method-kind-changed");
                    if (!Same(method.Value.GetProperty("idempotency"), other.GetProperty("idempotency")))
                        Fail(path + "/idempotency", "idempotency-changed");
                    var oldErrors = Strings(method.Value.GetProperty("errors"));
                    var newErrors = Strings(other.GetProperty("errors"));
                    if (!newErrors.IsSubsetOf(oldErrors)) Fail(path + "/errors", "method-error-added");
                    else if (!oldErrors.SetEquals(newErrors))
                        Change(SunderRpcContractChangeKind.Minor, path + "/errors", "method-error-removed");
                    Subset(method.Value.GetProperty("requestSchema"), other.GetProperty("requestSchema"), true, path + "/requestSchema");
                    if (oldKind == newKind)
                    {
                        var output = oldKind == "unary" ? "responseSchema" : "eventSchema";
                        Subset(method.Value.GetProperty(output), other.GetProperty(output), false, At(path, output));
                    }
                }
                foreach (var method in newMethods.EnumerateObject())
                {
                    Step();
                    if (!oldMethods.TryGetProperty(method.Name, out _))
                        Change(SunderRpcContractChangeKind.Minor, At(servicePath + "/methods", method.Name), "method-added");
                }
            }
            foreach (var service in newServices.EnumerateObject())
            {
                Step();
                if (!oldServices.TryGetProperty(service.Name, out _))
                    Change(SunderRpcContractChangeKind.Minor, At("/services", service.Name), "service-added");
            }
        }

        private void CompareErrors()
        {
            var oldErrors = previous.GetProperty("errors");
            var newErrors = candidate.GetProperty("errors");
            foreach (var error in oldErrors.EnumerateObject())
            {
                Step();
                var path = At("/errors", error.Name);
                if (!newErrors.TryGetProperty(error.Name, out var other))
                {
                    Fail(path, "error-removed");
                    continue;
                }
                var oldDetails = error.Value.TryGetProperty("detailsSchema", out var oldSchema);
                var newDetails = other.TryGetProperty("detailsSchema", out var newSchema);
                if (oldDetails != newDetails) Fail(path + "/detailsSchema", "error-details-presence-changed");
                else if (oldDetails) Subset(oldSchema, newSchema, false, path + "/detailsSchema");
            }
            foreach (var error in newErrors.EnumerateObject())
            {
                Step();
                if (!oldErrors.TryGetProperty(error.Name, out _))
                    Change(SunderRpcContractChangeKind.Minor, At("/errors", error.Name), "error-added");
            }
        }

        private bool Subset(JsonElement oldSchema, JsonElement newSchema, bool request, string path, int depth = 0)
        {
            Step(depth);
            if (Same(oldSchema, newSchema) && References(oldSchema).All(_equalDefinitions.Contains)) return true;

            var oldNullable = NullableBranch(oldSchema, _oldDefinitions, out var oldValue, out var oldNull);
            var newNullable = NullableBranch(newSchema, _newDefinitions, out var newValue, out var newNull);
            if (oldNullable || newNullable)
            {
                if ((request ? oldNullable : newNullable) && !(request ? newNullable : oldNullable))
                    return Fail(path, "null-not-accepted");
                var nullsCompatible = !oldNullable || !newNullable || Subset(oldNull, newNull, request, path + "/oneOf/0", depth + 1);
                return Subset(oldValue, newValue, request, path + "/oneOf/1", depth + 1) && nullsCompatible;
            }

            var oldRef = oldSchema.TryGetProperty("$ref", out var oldReference);
            var newRef = newSchema.TryGetProperty("$ref", out var newReference);
            if (oldRef || newRef)
            {
                if (!oldRef || !newRef || oldReference.GetString() != newReference.GetString())
                    return Fail(path, "reference-changed");
                var name = oldReference.GetString()!["#/$defs/".Length..];
                _examinedDefinitions.Add(name);
                if (_provedDefinitions.TryGetValue((name, request), out var proved)) return proved;
                var definitionPath = At("/$defs", name);
                if (HasCycle(name, _oldReferences, new(StringComparer.Ordinal), new(StringComparer.Ordinal))
                    || HasCycle(name, _newReferences, new(StringComparer.Ordinal), new(StringComparer.Ordinal)))
                {
                    _provedDefinitions[(name, request)] = false;
                    return Fail(definitionPath, "recursive-change-unclassified");
                }
                proved = Subset(_oldDefinitions.GetProperty(name), _newDefinitions.GetProperty(name), request, definitionPath, depth + 1);
                _provedDefinitions[(name, request)] = proved;
                if (proved) Change(SunderRpcContractChangeKind.Minor, definitionPath, "schema-compatible");
                return proved;
            }

            if (oldSchema.TryGetProperty("oneOf", out _) || newSchema.TryGetProperty("oneOf", out _))
                return Fail(path, "tagged-union-change-unclassified");
            var oldType = oldSchema.TryGetProperty("type", out var oldTypeValue) ? oldTypeValue.GetString() : null;
            var newType = newSchema.TryGetProperty("type", out var newTypeValue) ? newTypeValue.GetString() : null;
            if (oldType is null || oldType != newType) return Fail(path, "schema-form-changed");
            if (new[] { "format", "logicalType", "contentEncoding" }.Any(keyword =>
                    oldSchema.TryGetProperty(keyword, out _) || newSchema.TryGetProperty(keyword, out _)))
                return Fail(path, "exact-format-change-unclassified");

            var source = request ? oldSchema : newSchema;
            var target = request ? newSchema : oldSchema;
            switch (oldType)
            {
                case "string":
                    return LiteralsSubset(source, target, path)
                        & BoundsSubset(source, target, "minLength", "maxLength", path);
                case "boolean":
                case "null":
                    return LiteralsSubset(source, target, path);
                case "array":
                    return BoundsSubset(source, target, "minItems", "maxItems", path)
                        & Subset(oldSchema.GetProperty("items"), newSchema.GetProperty("items"), request, path + "/items", depth + 1);
                case "object":
                    var oldAdditional = oldSchema.TryGetProperty("additionalProperties", out var oldItems) ? oldItems.ValueKind : JsonValueKind.Undefined;
                    var newAdditional = newSchema.TryGetProperty("additionalProperties", out var newItems) ? newItems.ValueKind : JsonValueKind.Undefined;
                    if (oldAdditional != newAdditional || oldAdditional == JsonValueKind.Undefined)
                        return Fail(path, "object-form-change-unclassified");
                    var bounds = BoundsSubset(source, target, "minProperties", "maxProperties", path);
                    if (oldAdditional == JsonValueKind.Object)
                        return Subset(oldItems, newItems, request, path + "/additionalProperties", depth + 1)
                            & Subset(oldSchema.GetProperty("propertyNames"), newSchema.GetProperty("propertyNames"), request, path + "/propertyNames", depth + 1)
                            & bounds;
                    var sourceProperties = source.GetProperty("properties");
                    var targetProperties = target.GetProperty("properties");
                    var sourceRequired = Strings(source.GetProperty("required"));
                    var targetRequired = Strings(target.GetProperty("required"));
                    var valid = bounds;
                    foreach (var required in targetRequired.Order(StringComparer.Ordinal))
                        if (!sourceRequired.Contains(required)) valid = Fail(At(path + "/properties", required), "required-property-not-guaranteed");
                    foreach (var property in sourceProperties.EnumerateObject())
                    {
                        Step();
                        var propertyPath = At(path + "/properties", property.Name);
                        if (!targetProperties.TryGetProperty(property.Name, out var targetProperty))
                            valid = Fail(propertyPath, "property-not-accepted");
                        else
                            valid = Subset(request ? property.Value : targetProperty, request ? targetProperty : property.Value,
                                request, propertyPath, depth + 1) && valid;
                    }
                    return valid;
                default:
                    return Fail(path, "numeric-change-unclassified");
            }
        }

        private bool NullableBranch(JsonElement schema, JsonElement definitions, out JsonElement value, out JsonElement nullSchema)
        {
            value = schema;
            nullSchema = default;
            if (!schema.TryGetProperty("oneOf", out var variants) || variants.GetArrayLength() != 2) return false;
            for (var index = 0; index < 2; index++)
            {
                var resolved = variants[index];
                var depth = 0;
                while (resolved.TryGetProperty("$ref", out var reference))
                {
                    Step(++depth);
                    resolved = definitions.GetProperty(reference.GetString()!["#/$defs/".Length..]);
                }
                if (resolved.TryGetProperty("type", out var type) && type.GetString() == "null")
                {
                    value = variants[1 - index];
                    nullSchema = variants[index];
                    return true;
                }
            }
            return false;
        }

        private bool LiteralsSubset(JsonElement source, JsonElement target, string path)
        {
            foreach (var keyword in new[] { "const", "enum" })
            {
                var hasSource = source.TryGetProperty(keyword, out var sourceValues);
                var hasTarget = target.TryGetProperty(keyword, out var targetValues);
                if (hasSource != hasTarget) return Fail(At(path, keyword), "literal-form-changed");
                if (!hasSource) continue;
                if (keyword == "const")
                {
                    if (!Same(sourceValues, targetValues)) return Fail(path + "/const", "const-changed");
                }
                else
                {
                    var allowed = targetValues.EnumerateArray().Select(static value => value.GetRawText()).ToHashSet(StringComparer.Ordinal);
                    foreach (var value in sourceValues.EnumerateArray())
                    {
                        Step();
                        if (!allowed.Contains(value.GetRawText())) return Fail(path + "/enum", "enum-value-not-accepted");
                    }
                }
            }
            return true;
        }

        private bool BoundsSubset(JsonElement source, JsonElement target, string minimum, string maximum, string path)
        {
            var valid = true;
            var sourceMinimum = Bound(source, minimum, 0);
            var sourceMaximum = Bound(source, maximum, long.MaxValue);
            if (minimum == "minProperties" && source.TryGetProperty("additionalProperties", out var additional)
                && additional.ValueKind == JsonValueKind.False)
            {
                sourceMinimum = Math.Max(sourceMinimum, source.GetProperty("required").GetArrayLength());
                sourceMaximum = Math.Min(sourceMaximum, source.GetProperty("properties").GetPropertyCount());
            }
            if (sourceMinimum < Bound(target, minimum, 0))
                valid = Fail(At(path, minimum), "lower-bound-not-contained");
            if (sourceMaximum > Bound(target, maximum, long.MaxValue))
                valid = Fail(At(path, maximum), "upper-bound-not-contained");
            return valid;
        }

        private static long Bound(JsonElement schema, string name, long fallback)
            => schema.TryGetProperty(name, out var value) ? (long)value.GetDouble() : fallback;

        private static HashSet<string> Strings(JsonElement array)
            => array.EnumerateArray().Select(static value => value.GetString()!).ToHashSet(StringComparer.Ordinal);

        private static bool Same(JsonElement left, JsonElement right) => left.GetRawText() == right.GetRawText();
    }
}
