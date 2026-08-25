using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sunder.Host.Contracts;
using Sunder.Host.Supervisor;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Host.Supervisor.Tests;

public sealed class SupervisorAuthorityTests
{
    [Fact]
    public void RuntimeWorkerCoordinator_RemainsSoleMutableProcessAndLifecycleOwner()
    {
        var authorityTypes = new HashSet<Type>
        {
            typeof(Process),
            typeof(Stream),
            typeof(AnonymousPipeServerStream),
            typeof(RuntimeIpcEndpoint),
            typeof(RuntimeWorkerEndpointLease),
            typeof(RuntimeWorkerConnection),
            typeof(HostLifecycleStore),
            typeof(HostLifecyclePersistentState),
            typeof(HostOperationDescriptor),
            typeof(HostOperationState),
            typeof(HostRuntimeDesiredState),
            typeof(HostRuntimeState),
        };
        var authorityFields = typeof(RuntimeWorkerCoordinator).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<CompilerGeneratedAttribute>() is null)
            .SelectMany(DeclaredInstanceFields)
            .Where(field => field.GetCustomAttribute<CompilerGeneratedAttribute>() is null
                            && !field.IsInitOnly
                            && authorityTypes.Any(type => type.IsAssignableFrom(
                                Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType)))
            .Select(field => $"{field.DeclaringType!.Name}.{field.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "RuntimeWorkerCoordinator._connection",
                "RuntimeWorkerCoordinator._persistentState",
                "RuntimeWorkerCoordinator._process",
                "RuntimeWorkerCoordinator._state",
                "RuntimeWorkerCoordinator._supervisorLifetimePipe",
                "RuntimeWorkerCoordinator._workerEndpoint",
            },
            authorityFields);
    }

    [Fact]
    public void SupervisorHelpers_DoNotRetainMutableResourceOrLifecycleState()
    {
        AssertStatelessStaticHelper(typeof(RuntimeWorkerLaunchProbe));
        AssertStatelessStaticHelper(typeof(HostLifecycleTransitions));

        var resolverFields = DeclaredInstanceFields(typeof(RuntimeResourcePolicyLaunchResolver));
        Assert.Equal(
            new[] { "_policyFilePath", "_reportDiagnostic" },
            resolverFields.Select(field => field.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(resolverFields, field => Assert.True(field.IsInitOnly));
    }

    private static void AssertStatelessStaticHelper(Type helperType)
    {
        Assert.True(helperType.IsAbstract && helperType.IsSealed);
        Assert.DoesNotContain(
            helperType.GetFields(
                BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly),
            field => !field.IsLiteral);
    }

    private static FieldInfo[] DeclaredInstanceFields(Type type)
        => type.GetFields(
            BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly);
}
