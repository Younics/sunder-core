namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    public RuntimeManagementClient(Uri runtimeUrl, RuntimeClientPolicyOptions policy)
        : this(() => RuntimeConnectionInfoStore.LoadFor(runtimeUrl), policy: policy)
    {
    }
}
