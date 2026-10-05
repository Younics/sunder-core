using System.Reflection;
using System.Text.Json;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class RuntimeExecutionRuntimeContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static TheoryData<string, object, Type> Cases => new()
    {
        {
            "installed-runtime.json",
            Summary(),
            typeof(InstalledExecutionRuntimeSummary)
        },
        {
            "runtime-rebind-result.json",
            new ExecutionRuntimeRebindResult(
                true,
                null,
                "The package now uses the resolved execution runtime.",
                "sunder.package.sample",
                "1.2.3",
                "package-content-identity-123",
                Binding(Identity("10.0.0", 1)),
                Binding(Identity()),
                true,
                Impact()),
            typeof(ExecutionRuntimeRebindResult)
        },
        {
            "runtime-ensure-result.json",
            new RuntimeExecutionRuntimeOperationResult(
                true,
                null,
                "The execution runtime is ready.",
                RuntimeExecutionRuntimeOperationActions.Ensure,
                Summary(),
                false),
            typeof(RuntimeExecutionRuntimeOperationResult)
        },
        {
            "runtime-import-result.json",
            new RuntimeExecutionRuntimeOperationResult(
                true,
                null,
                "The trusted execution runtime was imported.",
                RuntimeExecutionRuntimeOperationActions.Import,
                Summary(RuntimeExecutionRuntimeAcquisitionSources.LocalImport),
                false),
            typeof(RuntimeExecutionRuntimeOperationResult)
        },
        {
            "runtime-store-fault.json",
            new RuntimeExecutionRuntimeStoreFault(
                "execution-runtime.integrity-failed",
                "Execution runtime integrity verification failed.",
                false,
                RuntimeExecutionRuntimeOperationActions.Verify,
                "sunder.runtime.dotnet",
                "10.0.1",
                2,
                "win-x64",
                "sunder.package.sample",
                "runtime",
                DateTimeOffset.Parse("2026-07-11T10:10:00Z")),
            typeof(RuntimeExecutionRuntimeStoreFault)
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ExecutionRuntimePayload_MatchesGoldenJson(string fixtureName, object payload, Type payloadType)
    {
        var actual = JsonSerializer.Serialize(payload, payloadType, JsonOptions).ReplaceLineEndings("\n");
        var updateRoot = Environment.GetEnvironmentVariable("SUNDER_UPDATE_RUNTIME_CONTRACT_GOLDENS");
        if (!string.IsNullOrWhiteSpace(updateRoot))
        {
            Directory.CreateDirectory(updateRoot);
            File.WriteAllText(Path.Combine(updateRoot, fixtureName), actual + Environment.NewLine);
            return;
        }

        var fixturePath = Path.Combine(AppContext.BaseDirectory, "RuntimeContracts", fixtureName);
        var expected = File.ReadAllText(fixturePath).ReplaceLineEndings("\n").TrimEnd();
        Assert.Equal(expected, actual);
        Assert.NotNull(JsonSerializer.Deserialize(expected, payloadType, JsonOptions));
    }

    [Fact]
    public void RuntimeApiRoutes_AreStableAndEscapeIdentities()
    {
        Assert.Equal("/api/v1", RuntimeApiRoutes.Root);
        Assert.Equal("/api/v1/execution-runtimes", RuntimeApiRoutes.ExecutionRuntimes);
        Assert.Equal("/api/v1/execution-runtimes/ensure", RuntimeApiRoutes.ExecutionRuntimeEnsure);
        Assert.Equal("/api/v1/execution-runtimes/import", RuntimeApiRoutes.ExecutionRuntimeImport);
        Assert.Equal("/api/v1/execution-runtimes/gc", RuntimeApiRoutes.ExecutionRuntimeGarbageCollection);
        Assert.Equal(
            "/api/v1/execution-runtimes/sunder.runtime.dotnet/versions/10.0.1/revisions/2/win-x64/abis/sunder.dotnet-worker-module.v1",
            RuntimeApiRoutes.ExecutionRuntime(
                "sunder.runtime.dotnet",
                "10.0.1",
                2,
                "win-x64",
                "sunder.dotnet-worker-module.v1"));
        Assert.Equal(
            "/api/v1/execution-runtimes/sunder.runtime.dotnet/versions/10.0.1/revisions/2/win-x64/abis/sunder.dotnet-worker-module.v1/references",
            RuntimeApiRoutes.ExecutionRuntimeReferences(
                "sunder.runtime.dotnet",
                "10.0.1",
                2,
                "win-x64",
                "sunder.dotnet-worker-module.v1"));
        Assert.Equal(
            "/api/v1/execution-runtimes/sunder.runtime.dotnet/versions/10.0.1/revisions/2/win-x64/abis/sunder.dotnet-worker-module.v1/impact",
            RuntimeApiRoutes.ExecutionRuntimeImpact(
                "sunder.runtime.dotnet",
                "10.0.1",
                2,
                "win-x64",
                "sunder.dotnet-worker-module.v1"));
        Assert.Equal(
            "/api/v1/execution-runtimes/sunder.runtime.dotnet/versions/10.0.1/revisions/2/win-x64/abis/sunder.dotnet-worker-module.v1/verify",
            RuntimeApiRoutes.ExecutionRuntimeVerify(
                "sunder.runtime.dotnet",
                "10.0.1",
                2,
                "win-x64",
                "sunder.dotnet-worker-module.v1"));
        Assert.Equal(
            "/api/v1/execution-runtimes/sunder.runtime.dotnet/versions/10.0.1/revisions/2/win-x64/abis/sunder.dotnet-worker-module.v1/repair",
            RuntimeApiRoutes.ExecutionRuntimeRepair(
                "sunder.runtime.dotnet",
                "10.0.1",
                2,
                "win-x64",
                "sunder.dotnet-worker-module.v1"));
        Assert.Equal(
            "/api/v1/packages/sample%2Fpackage/execution-runtime-bindings",
            RuntimeApiRoutes.PackageExecutionRuntimeBindings("sample/package"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeApiRoutes.ExecutionRuntime(
            "sunder.runtime.dotnet",
            "10.0.1",
            0,
            "win-x64",
            "sunder.dotnet-worker-module.v1"));
        Assert.Throws<ArgumentException>(() => RuntimeApiRoutes.PackageExecutionRuntimeBindings(".."));
    }

    [Fact]
    public void ExecutionRuntimeDtoNullability_IsIntentional()
    {
        var context = new NullabilityInfoContext();
        Assert.Equal(NullabilityState.NotNull, context.Create(typeof(InstalledExecutionRuntimeSummary).GetProperty(nameof(InstalledExecutionRuntimeSummary.Identity))!).ReadState);
        Assert.Equal(NullabilityState.NotNull, context.Create(typeof(InstalledPackageDescriptor).GetProperty(nameof(InstalledPackageDescriptor.ExecutionRuntimeBindings))!).ReadState);
        Assert.Equal(NullabilityState.Nullable, context.Create(typeof(RuntimeExecutionRuntimeLifecycle).GetProperty(nameof(RuntimeExecutionRuntimeLifecycle.PolicyMessage))!).ReadState);
        Assert.Equal(NullabilityState.Nullable, context.Create(typeof(RuntimeExecutionRuntimeOperationResult).GetProperty(nameof(RuntimeExecutionRuntimeOperationResult.ErrorCode))!).ReadState);
        Assert.Equal(NullabilityState.Nullable, context.Create(typeof(RuntimeExecutionRuntimeOperationResult).GetProperty(nameof(RuntimeExecutionRuntimeOperationResult.Impact))!).ReadState);
        Assert.Equal(NullabilityState.Nullable, context.Create(typeof(ExecutionRuntimeRebindResult).GetProperty(nameof(ExecutionRuntimeRebindResult.PreviousBinding))!).ReadState);
    }

    [Fact]
    public void RuntimeImpact_FreezesExactImpactedPackageIds()
    {
        string[] packageIds = ["sunder.package.sample", "sunder.package.tools"];

        var impact = new RuntimeExecutionRuntimeImpact(2, packageIds, new string('f', 64));
        packageIds[0] = "mutated";

        Assert.Equal(["sunder.package.sample", "sunder.package.tools"], impact.ImpactedPackageIds);
    }

    [Fact]
    public void RuntimeDtos_DoNotExposeStorePathsTokensOrRegistryErrorConstants()
    {
        var executionRuntimeTypes = typeof(InstalledExecutionRuntimeSummary).Assembly
            .GetExportedTypes()
            .Where(static type => type.Name.Contains("ExecutionRuntime", StringComparison.Ordinal))
            .ToArray();
        var propertyNames = executionRuntimeTypes
            .SelectMany(static type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(static property => property.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, static name => name.Contains("Path", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, static name => name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            executionRuntimeTypes.SelectMany(static type => type.GetFields(BindingFlags.Public | BindingFlags.Static)),
            static field => field.Name.EndsWith("Failed", StringComparison.Ordinal) || field.Name.EndsWith("Invalid", StringComparison.Ordinal));
    }

    private static RuntimeExecutionRuntimeRequirement Requirement()
        => new("sunder.runtime.dotnet", ">=10.0.0 <11.0.0", "sunder.dotnet-worker-module.v1");

    private static RuntimeExecutionRuntimeImpact Impact()
        => new(
            3,
            ["sunder.package.sample", "sunder.package.tools", "sunder.package.workflow"],
            new string('f', 64));

    private static RuntimeExecutionRuntimeIdentity Identity(string version = "10.0.1", int packagingRevision = 2)
        => new(
            "sunder.runtime.dotnet",
            version,
            packagingRevision,
            "win-x64",
            "sunder.dotnet-worker-module.v1",
            $"runtime-manifest-{version}-{packagingRevision}",
            $"runtime-artifact-{version}-{packagingRevision}");

    private static RuntimeExecutionRuntimeLifecycle Lifecycle()
        => new(
            "supported",
            DateTimeOffset.Parse("2026-07-10T12:00:00Z"),
            null,
            null,
            null,
            null,
            null,
            42,
            DateTimeOffset.Parse("2026-07-11T10:00:00Z"));

    private static RuntimeExecutionRuntimeHealth Health()
        => new(
            RuntimeExecutionRuntimeStatuses.Ready,
            DateTimeOffset.Parse("2026-07-11T10:01:00Z"),
            false,
            null);

    private static InstalledExecutionRuntimeSummary Summary(
        string acquisitionSource = RuntimeExecutionRuntimeAcquisitionSources.Registry)
        => new(
            Identity(),
            RuntimeExecutionRuntimeStatuses.Ready,
            104857600,
            3,
            acquisitionSource,
            "sunder-runtime-release-2026-01",
            2,
            Health(),
            Lifecycle(),
            DateTimeOffset.Parse("2026-07-11T09:00:00Z"));

    private static PackageTargetExecutionRuntimeBinding Binding(RuntimeExecutionRuntimeIdentity identity)
        => new(
            "runtime",
            "win-x64",
            Requirement(),
            identity,
            104857600,
            "https://registry.example",
            RuntimeExecutionRuntimeAcquisitionSources.Registry,
            "sunder-runtime-release-2026-01",
            2,
            Lifecycle(),
            DateTimeOffset.Parse("2026-07-11T09:00:00Z"));
}
