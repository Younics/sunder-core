using System.ComponentModel;
using System.Reflection;
using System.Xml.Linq;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed partial class SunderServiceResourceTests
{
    [Fact]
    public void PublicResourceTypesRetainRpcCapabilityDocumentationAndCodegenVisibility()
    {
        // These annotations drive package capability inference; a snapshot alone must not bless their removal.
        Type[] authorTypes = [typeof(SunderContent), typeof(ServiceReference<>), typeof(IServiceProviders<>),
            typeof(ServiceProvider<>), typeof(ServiceProviderCatalog<>), typeof(ServiceProviderCatalogEvent<>),
            typeof(ServiceProviderState), typeof(ServiceProviderChangeKind)];
        Type[] helperTypes = [typeof(SunderRpcResourceLifetime), typeof(SunderRpcMarshallingContext), typeof(SunderRpcServiceBindingLifetime), typeof(SunderRpcServiceCall)];
        var documentation = XDocument.Load(Path.ChangeExtension(typeof(SunderContent).Assembly.Location, ".xml"))
            .Descendants("member").ToDictionary(member => (string)member.Attribute("name")!, StringComparer.Ordinal);
        foreach (var type in authorTypes.Concat(helperTypes))
        {
            var capability = Assert.Single(type.GetCustomAttributes<SunderSdkCapabilityAttribute>());
            Assert.Equal(SunderSdkCapabilities.RpcV1, capability.Capability);
            var summary = documentation["T:" + type.FullName].Element("summary")?.Value;
            Assert.False(string.IsNullOrWhiteSpace(summary), $"Missing public XML summary for {type}.");
            Assert.Equal(helperTypes.Contains(type),
                type.GetCustomAttribute<EditorBrowsableAttribute>()?.State == EditorBrowsableState.Never);
        }

        // These resource/helper types contain authored members only. Verify the shipped XML,
        // including generic methods and both original cancellation tokens, rather than source comments.
        foreach (var type in new[] { typeof(SunderContent), typeof(ServiceReference<>),
                     typeof(SunderRpcResourceLifetime), typeof(SunderRpcMarshallingContext), typeof(SunderRpcServiceBindingLifetime), typeof(SunderRpcServiceCall) })
        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            var prefix = member switch
            {
                ConstructorInfo => "M:" + type.FullName + ".#ctor",
                MethodInfo method when !method.IsSpecialName => "M:" + type.FullName + "." + method.Name,
                PropertyInfo => "P:" + type.FullName + "." + member.Name,
                _ => null,
            };
            if (prefix is null) continue;
            Assert.Contains(documentation, entry =>
                (entry.Key == prefix || entry.Key.StartsWith(prefix + "(", StringComparison.Ordinal)
                    || entry.Key.StartsWith(prefix + "``", StringComparison.Ordinal))
                && !string.IsNullOrWhiteSpace(entry.Value.Element("summary")?.Value));
        }
    }
}
