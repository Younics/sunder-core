using Sunder.App.Bridge;
using System.Threading.Tasks;

public sealed class EntryPoint : IAppPackageEntryPointV1
{
    public ValueTask<IAppPackageActivationV1> CreateAsync(IAppPackageHostV1 host, IAppContributionSinkV1 sink)
#if SDK_TWO
        => ValueTask.FromResult(IncompatibleSdk.PackageSdk.ActivateV2(2, host, sink));
#else
        => ValueTask.FromResult(IncompatibleSdk.PackageSdk.ActivateV1("one", host, sink));
#endif
}
