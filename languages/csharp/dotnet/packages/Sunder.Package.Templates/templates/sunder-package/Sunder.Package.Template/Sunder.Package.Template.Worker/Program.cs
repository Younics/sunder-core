using Sunder.Package.Template.Protocol;
using Sunder.Package.Template.Worker;
using Sunder.Sdk.Worker;

var descriptor = PackageProtocol.Sample;
var providers = new List<SunderWorkerProviderRegistration>
{
    new(
        "SUNDER_PACKAGE_ID_TEXT.sample",
        descriptor.ContractId,
        descriptor.Version,
        descriptor.Sha256,
        descriptor.AbiSha256 ?? throw new InvalidDataException("The RPC V2 descriptor is missing its ABI hash."),
        new SampleRpcProvider()),
};
WorkerConfiguration.AddOptionalProviders(providers);

await SunderWorker.RunAsync(_ => new SunderWorkerOptions(providers));

namespace Sunder.Package.Template.Worker
{
    internal static partial class WorkerConfiguration
    {
        public static void AddOptionalProviders(List<SunderWorkerProviderRegistration> providers)
            => AddStackProviders(providers);

        static partial void AddStackProviders(List<SunderWorkerProviderRegistration> providers);
    }
}
