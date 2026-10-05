using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Storage;
using Sunder.Sdk.Worker;
using Sunder.Sdk.Worker.Internal;
using Sunder.Sdk.Worker.Tests.TestSupport;

namespace Sunder.Sdk.Worker.Tests;

public sealed class WorkerV1ConformanceCorpusTests
{
    [Fact]
    public async Task DotNetWorker_ExecutesSharedWorkerV1Corpus()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "WorkerV1Conformance", "corpus.json")));
        var root = corpus.RootElement;
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(WorkerProtocol.Name, root.GetProperty("protocol").GetProperty("identity").GetString());
        Assert.Equal(WorkerProtocol.Version, root.GetProperty("protocol").GetProperty("version").GetInt32());
        var categories = root.GetProperty("categories");
        Assert.Equal(
            [
                "shutdown-order",
                "framing",
                "resource-profile",
                "transport-bounds",
                "json",
                "safe-integers",
                "data",
                "logging",
                "migration",
                "callback",
                "auth",
                "content",
                "scope",
                "cancel",
            ],
            categories.EnumerateObject().Select(static category => category.Name));
        Assert.Equal(
            [
                "cancel-and-drain-worker-originated-calls",
                "close-scopes-and-release-content-handles",
                "invoke-on-shutdown",
                "close-and-drain-logging-lane",
                "write-and-flush-shutdown-ack",
            ],
            categories.GetProperty("shutdown-order").GetProperty("requirements")
                .EnumerateArray().Select(static requirement => requirement.GetString()));
        Assert.All(categories.EnumerateObject(), static category =>
            Assert.NotEmpty(category.Value.GetProperty("requirements").EnumerateArray()));

        foreach (var fixture in root.GetProperty("safeIntegerTokens").EnumerateArray())
        {
            var token = fixture.GetProperty("token").GetString()!;
            using var document = JsonDocument.Parse($"{{\"value\":{token}}}");
            var exception = Record.Exception(() =>
                WorkerJson.RequiredNonNegativeInt64(document.RootElement, "value"));
            Assert.Equal(fixture.GetProperty("valid").GetBoolean(), exception is null);
            if (exception is not null)
            {
                Assert.IsType<WorkerProtocolException>(exception);
            }
        }

        var lifecycle = root.GetProperty("lifecycle");
        var rpc = root.GetProperty("rpc");
        var packageData = root.GetProperty("packageData");
        var unary = rpc.GetProperty("unary");
        var stream = rpc.GetProperty("stream");
        var domainFault = rpc.GetProperty("domainFault");
        var undeclaredDomainFault = rpc.GetProperty("undeclaredDomainFault");
        var storageMigrations = root.GetProperty("storageMigrations");
        var envelopePropertySets = root.GetProperty("envelopePropertySets");
        var expectedEvents = stream.GetProperty("events").EnumerateArray()
            .Select(static item => item.Clone())
            .ToArray();
        var handler = new TestRpcHandler
        {
            DeclaredErrors = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal)
            {
                [rpc.GetProperty("serviceId").GetString()!] = rpc.GetProperty("declaredErrors")
                    .EnumerateObject()
                    .ToDictionary(
                        static property => property.Name,
                        static property => (IReadOnlyList<string>)property.Value.EnumerateArray()
                            .Select(static value => value.GetString()!)
                            .ToArray(),
                        StringComparer.Ordinal),
            },
            Unary = (_, serviceId, methodId, request, _) =>
            {
                Assert.Equal(rpc.GetProperty("serviceId").GetString(), serviceId);
                if (methodId == domainFault.GetProperty("methodId").GetString())
                {
                    var expected = domainFault.GetProperty("error");
                    return ValueTask.FromException<JsonElement>(new SunderRpcException(new SunderRpcError(
                        SunderRpcErrorKind.Domain,
                        expected.GetProperty("code").GetString()!,
                        expected.GetProperty("message").GetString()!,
                        details: expected.GetProperty("details"))));
                }
                if (methodId == undeclaredDomainFault.GetProperty("methodId").GetString())
                {
                    var thrown = undeclaredDomainFault.GetProperty("thrownError");
                    return ValueTask.FromException<JsonElement>(new SunderRpcException(new SunderRpcError(
                        SunderRpcErrorKind.Domain,
                        thrown.GetProperty("code").GetString()!,
                        thrown.GetProperty("message").GetString()!)));
                }
                Assert.Equal(unary.GetProperty("methodId").GetString(), methodId);
                return ValueTask.FromResult(JsonSerializer.SerializeToElement(new
                {
                    accepted = true,
                    request = request.Clone(),
                }));
            },
            Stream = (_, serviceId, methodId, request, cancellationToken) => Events(
                rpc.GetProperty("serviceId").GetString()!,
                stream.GetProperty("methodId").GetString()!,
                serviceId,
                methodId,
                request,
                expectedEvents,
                cancellationToken),
        };
        await using var worker = new WorkerHarness(context => new SunderWorkerOptions(
            [WorkerHarness.Registration(handler)])
        {
            StateMigrations = CreateMigrations(storageMigrations, "state"),
            SecretMigrations = CreateMigrations(storageMigrations, "secrets"),
            OnActivated = async (_, cancellationToken) =>
            {
                var key = packageData.GetProperty("key").GetString()!;
                var value = packageData.GetProperty("value").GetString()!;
                await context.State.SetValueAsync(key, value, cancellationToken);
                Assert.Equal(value, await context.State.GetValueAsync(key, cancellationToken));
                await context.State.DeleteValueAsync(key, cancellationToken);
            },
        });

        await worker.HandshakeAsync(lifecycle.GetProperty("challenge").GetString()!);
        Assert.True(JsonElement.DeepEquals(
            storageMigrations.GetProperty("plans"),
            worker.ReadyContributions!.Value.GetProperty("storageMigrations")));
        var generation = lifecycle.GetProperty("sessionGeneration").GetInt64();
        await worker.StartCandidateAndCommitAsync(generation);
        await worker.PrepareStorageMigrationsAsync(generation);
        await worker.ActivateAsync(generation);
        await CompleteStorageMigrationsAsync(worker, storageMigrations, envelopePropertySets);
        await CompletePackageDataRoundTripAsync(worker, packageData, envelopePropertySets);
        using (var activated = await worker.ReadAsync())
        {
            AssertEnvelopePropertySet(activated.RootElement, envelopePropertySets);
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
            Assert.Equal(generation, activated.RootElement.GetProperty("sessionGeneration").GetInt64());
        }

        await worker.SendInvocationAsync(
            unary.GetProperty("id").GetString()!,
            "unary",
            unary.GetProperty("methodId").GetString()!,
            unary.GetProperty("request").Clone(),
            generation);
        using (var result = await worker.ReadAsync())
        {
            AssertEnvelopePropertySet(result.RootElement, envelopePropertySets);
            Assert.Equal("worker.result", result.RootElement.GetProperty("type").GetString());
            Assert.True(JsonElement.DeepEquals(
                unary.GetProperty("result"),
                result.RootElement.GetProperty("value")));
        }

        await worker.SendInvocationAsync(
            stream.GetProperty("id").GetString()!,
            "server-stream",
            stream.GetProperty("methodId").GetString()!,
            stream.GetProperty("request").Clone(),
            generation);
        foreach (var expected in expectedEvents)
        {
            using var item = await worker.ReadAsync();
            AssertEnvelopePropertySet(item.RootElement, envelopePropertySets);
            Assert.Equal("worker.event", item.RootElement.GetProperty("type").GetString());
            Assert.True(JsonElement.DeepEquals(expected, item.RootElement.GetProperty("value")));
        }
        using (var complete = await worker.ReadAsync())
        {
            AssertEnvelopePropertySet(complete.RootElement, envelopePropertySets);
            Assert.Equal("worker.complete", complete.RootElement.GetProperty("type").GetString());
        }

        await worker.SendInvocationAsync(
            domainFault.GetProperty("id").GetString()!,
            "unary",
            domainFault.GetProperty("methodId").GetString()!,
            domainFault.GetProperty("request").Clone(),
            generation);
        using (var fault = await worker.ReadAsync())
        {
            AssertEnvelopePropertySet(fault.RootElement, envelopePropertySets);
            Assert.Equal("worker.error", fault.RootElement.GetProperty("type").GetString());
            var expected = domainFault.GetProperty("error");
            var actual = fault.RootElement.GetProperty("error");
            Assert.Equal(expected.GetProperty("kind").GetString(), actual.GetProperty("kind").GetString());
            Assert.Equal(expected.GetProperty("code").GetString(), actual.GetProperty("code").GetString());
            Assert.Equal(expected.GetProperty("message").GetString(), actual.GetProperty("message").GetString());
            Assert.True(JsonElement.DeepEquals(
                expected.GetProperty("details"),
                actual.GetProperty("details")));
        }

        await worker.SendInvocationAsync(
            undeclaredDomainFault.GetProperty("id").GetString()!,
            "unary",
            undeclaredDomainFault.GetProperty("methodId").GetString()!,
            undeclaredDomainFault.GetProperty("request").Clone(),
            generation);
        using (var diagnostic = await worker.ReadAsync())
        {
            AssertEnvelopePropertySet(diagnostic.RootElement, envelopePropertySets);
            Assert.True(undeclaredDomainFault.GetProperty("diagnosticRequired").GetBoolean());
            Assert.Equal(
                "worker.provider-fault-diagnostic",
                diagnostic.RootElement.GetProperty("type").GetString());
            Assert.Equal(
                undeclaredDomainFault.GetProperty("id").GetString(),
                diagnostic.RootElement.GetProperty("invocationId").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = diagnostic.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var fault = await worker.ReadAsync())
        {
            AssertEnvelopePropertySet(fault.RootElement, envelopePropertySets);
            Assert.Equal("worker.error", fault.RootElement.GetProperty("type").GetString());
            var expected = undeclaredDomainFault.GetProperty("error");
            var actual = fault.RootElement.GetProperty("error");
            Assert.Equal(expected.GetProperty("kind").GetString(), actual.GetProperty("kind").GetString());
            Assert.Equal(expected.GetProperty("code").GetString(), actual.GetProperty("code").GetString());
            Assert.Equal(expected.GetProperty("message").GetString(), actual.GetProperty("message").GetString());
        }

        await worker.SendAsync(new
        {
            type = "host.shutdown",
            shutdownId = lifecycle.GetProperty("shutdownId").GetString(),
            reason = lifecycle.GetProperty("shutdownReason").GetString(),
        });
        using (var shutdown = await worker.ReadAsync())
        {
            AssertEnvelopePropertySet(shutdown.RootElement, envelopePropertySets);
            Assert.Equal("worker.shutdown-ack", shutdown.RootElement.GetProperty("type").GetString());
            Assert.Equal(lifecycle.GetProperty("shutdownId").GetString(), shutdown.RootElement.GetProperty("shutdownId").GetString());
        }
        await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static IReadOnlyList<PackageStorageKeyMigration> CreateMigrations(
        JsonElement storageMigrations,
        string store)
    {
        var plan = storageMigrations.GetProperty("plans").EnumerateArray()
            .Single(candidate => candidate.GetProperty("store").GetString() == store);
        return plan.GetProperty("rules").EnumerateArray().Select(rule =>
        {
            var kind = rule.GetProperty("kind").GetString();
            return kind switch
            {
                "exact" => PackageStorageKeyMigration.Exact(
                    rule.GetProperty("legacyKey").GetString()!,
                    rule.GetProperty("destinationKey").GetString()!),
                "opaque-id" => PackageStorageKeyMigration.OpaqueId(
                    rule.GetProperty("legacyPrefix").GetString()!,
                    rule.GetProperty("legacySuffix").GetString()!,
                    rule.GetProperty("destinationPrefix").GetString()!,
                    rule.GetProperty("destinationVersion").GetInt32()),
                "opaque-id-json" => PackageStorageKeyMigration.OpaqueIdWithJsonIdentity(
                    rule.GetProperty("legacyPrefix").GetString()!,
                    rule.GetProperty("legacySuffix").GetString()!,
                    rule.GetProperty("destinationPrefix").GetString()!,
                    rule.GetProperty("destinationVersion").GetInt32(),
                    rule.GetProperty("identityProperty").GetString()!),
                "opaque-id-json-case-insensitive" =>
                    PackageStorageKeyMigration.OpaqueIdWithCaseInsensitiveJsonIdentity(
                        rule.GetProperty("legacyPrefix").GetString()!,
                        rule.GetProperty("legacySuffix").GetString()!,
                        rule.GetProperty("destinationPrefix").GetString()!,
                        rule.GetProperty("destinationVersion").GetInt32(),
                        rule.GetProperty("identityProperty").GetString()!),
                "dynamic-cleanup" => PackageStorageKeyMigration.DynamicCleanup(key =>
                    ResolveDynamicAction(storageMigrations, key)),
                _ => throw new InvalidDataException($"Unsupported conformance migration kind '{kind}'."),
            };
        }).ToArray();
    }

    private static PackageStorageKeyMigrationAction ResolveDynamicAction(
        JsonElement storageMigrations,
        string key)
    {
        var action = storageMigrations.GetProperty("dynamicActions").EnumerateArray()
            .SingleOrDefault(candidate => candidate.GetProperty("key").GetString() == key);
        if (action.ValueKind == JsonValueKind.Undefined) return PackageStorageKeyMigrationAction.NoMatch;
        return action.GetProperty("kind").GetString() switch
        {
            "no-match" => PackageStorageKeyMigrationAction.NoMatch,
            "delete" => PackageStorageKeyMigrationAction.Delete,
            "rewrite" => PackageStorageKeyMigrationAction.Rewrite(
                action.GetProperty("destinationKey").GetString()!,
                action.GetProperty("precedence").GetInt32()),
            var kind => throw new InvalidDataException($"Unsupported conformance migration action '{kind}'."),
        };
    }

    private static async Task CompleteStorageMigrationsAsync(
        WorkerHarness worker,
        JsonElement storageMigrations,
        JsonElement envelopePropertySets)
    {
        foreach (var plan in storageMigrations.GetProperty("plans").EnumerateArray())
        {
            var expectedRules = plan.GetProperty("rules");
            if (expectedRules.EnumerateArray().Any(static rule =>
                    rule.GetProperty("kind").GetString() == "dynamic-cleanup"))
            {
                using var keys = await worker.ReadAsync();
                AssertEnvelopePropertySet(keys.RootElement, envelopePropertySets);
                Assert.Equal("worker.migration-keys", keys.RootElement.GetProperty("type").GetString());
                Assert.Equal(plan.GetProperty("store").GetString(), keys.RootElement.GetProperty("store").GetString());
                await worker.SendAsync(new
                {
                    type = "host.result",
                    id = keys.RootElement.GetProperty("id").GetString(),
                    value = new
                    {
                        keys = storageMigrations.GetProperty("dynamicKeys").EnumerateArray()
                            .Select(static key => key.GetString())
                            .ToArray(),
                    },
                });
            }

            using var apply = await worker.ReadAsync();
            AssertEnvelopePropertySet(apply.RootElement, envelopePropertySets);
            Assert.Equal("worker.migration-apply", apply.RootElement.GetProperty("type").GetString());
            Assert.Equal(plan.GetProperty("store").GetString(), apply.RootElement.GetProperty("store").GetString());
            var actualRules = apply.RootElement.GetProperty("rules").EnumerateArray().ToArray();
            var declarations = expectedRules.EnumerateArray().ToArray();
            Assert.Equal(declarations.Length, actualRules.Length);
            for (var index = 0; index < declarations.Length; index++)
            {
                if (declarations[index].GetProperty("kind").GetString() == "dynamic-cleanup")
                {
                    Assert.Equal("dynamic-cleanup", actualRules[index].GetProperty("kind").GetString());
                    Assert.True(JsonElement.DeepEquals(
                        storageMigrations.GetProperty("dynamicActions"),
                        actualRules[index].GetProperty("actions")));
                }
                else
                {
                    Assert.True(JsonElement.DeepEquals(declarations[index], actualRules[index]));
                }
            }
            await worker.SendAsync(new
            {
                type = "host.result",
                id = apply.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
    }

    [Fact]
    [Trait("Category", "Conformance")]
    [Trait("Size", "Small")]
    public void SharedEnvelopePropertyOracle_RejectsSeededExtraProperty()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "WorkerV1Conformance", "corpus.json")));
        using var mutated = JsonDocument.Parse("""{"type":"worker.result","id":"one","value":null,"seeded":true}""");

        Assert.ThrowsAny<Exception>(() => AssertEnvelopePropertySet(
            mutated.RootElement,
            corpus.RootElement.GetProperty("envelopePropertySets")));
    }

    private static void AssertEnvelopePropertySet(JsonElement envelope, JsonElement propertySets)
    {
        var type = envelope.GetProperty("type").GetString()!;
        var expected = propertySets.GetProperty(type).EnumerateArray()
            .Select(static property => property.GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = envelope.EnumerateObject().Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    private static async Task CompletePackageDataRoundTripAsync(
        WorkerHarness worker,
        JsonElement packageData,
        JsonElement envelopePropertySets)
    {
        var key = packageData.GetProperty("key").GetString();
        var value = packageData.GetProperty("value").GetString();
        using (var set = await worker.ReadAsync())
        {
            AssertEnvelopePropertySet(set.RootElement, envelopePropertySets);
            Assert.Equal("worker.state-set", set.RootElement.GetProperty("type").GetString());
            Assert.Equal(key, set.RootElement.GetProperty("key").GetString());
            Assert.Equal(value, set.RootElement.GetProperty("value").GetProperty("value").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = set.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var get = await worker.ReadAsync())
        {
            AssertEnvelopePropertySet(get.RootElement, envelopePropertySets);
            Assert.Equal("worker.state-get", get.RootElement.GetProperty("type").GetString());
            Assert.Equal(key, get.RootElement.GetProperty("key").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = get.RootElement.GetProperty("id").GetString(),
                value = new { kind = "inline", value },
            });
        }
        using (var delete = await worker.ReadAsync())
        {
            AssertEnvelopePropertySet(delete.RootElement, envelopePropertySets);
            Assert.Equal("worker.state-delete", delete.RootElement.GetProperty("type").GetString());
            Assert.Equal(key, delete.RootElement.GetProperty("key").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = delete.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
    }

    private static async IAsyncEnumerable<JsonElement> Events(
        string expectedServiceId,
        string expectedMethodId,
        string serviceId,
        string methodId,
        JsonElement request,
        IReadOnlyList<JsonElement> expectedEvents,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Assert.Equal(expectedServiceId, serviceId);
        Assert.Equal(expectedMethodId, methodId);
        foreach (var expected in expectedEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(JsonElement.DeepEquals(
                expected.GetProperty("request"),
                request));
            yield return expected.Clone();
            await Task.Yield();
        }
    }
}
