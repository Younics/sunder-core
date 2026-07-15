using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;

namespace Sunder.Package.Hosting;

internal sealed class ConstrainedPackageServiceCollection(IEnumerable<Type> reservedServiceTypes)
    : Collection<ServiceDescriptor>, IServiceCollection
{
    private readonly HashSet<Type> _reservedServiceTypes = new(reservedServiceTypes);

    protected override void InsertItem(int index, ServiceDescriptor item)
    {
        Validate(item);
        base.InsertItem(index, item);
    }

    protected override void SetItem(int index, ServiceDescriptor item)
    {
        Validate(item);
        base.SetItem(index, item);
    }

    public void CopyTo(IServiceCollection destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        foreach (var descriptor in this)
        {
            destination.Add(descriptor);
        }
    }

    private void Validate(ServiceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (_reservedServiceTypes.Contains(descriptor.ServiceType)
            || descriptor.ServiceType.IsGenericType
               && _reservedServiceTypes.Contains(descriptor.ServiceType.GetGenericTypeDefinition()))
        {
            throw new InvalidOperationException(
                $"Package service registration for reserved host capability '{descriptor.ServiceType.FullName}' is not allowed.");
        }
    }
}
