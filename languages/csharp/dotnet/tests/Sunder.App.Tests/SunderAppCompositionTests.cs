using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Composition;
using Sunder.App.Models;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class SunderAppCompositionTests
{
    [Fact]
    public void StartupServices_ValidateWithoutAnExistingShellSession()
    {
        // Validate the real registration graph before any session-owned objects
        // exist. Factory delegates stay lazy, so this touches no user state or Runtime.
        var services = SunderAppComposition.CreateServices(
            new Application(), new AppStartupOptions(), new AppPackageResourceAssemblyRegistry());

        Assert.Contains(services, service => service.ServiceType == typeof(AppProcessPolicyLifetime));
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(AppProcessPolicySupervisor)
            || service.ServiceType == typeof(AppRuntimeProcessTransport));
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(AppProcessPolicyLifetime)));
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(typeof(AppProcessPolicyLifetime)));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }
}
