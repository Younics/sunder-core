using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Package.Build.Rpc.Authoring;

namespace Sunder.Package.Build.Tasks;

public sealed partial class ReadSunderPackageRoleInputsTask
{
    [Output] public ITaskItem[] BindingSnapshots { get; private set; } = [];
    [Output] public ITaskItem[] ContractBundles { get; private set; } = [];
    [Output] public ITaskItem[] RpcProviders { get; private set; } = [];
    [Output] public ITaskItem[] RpcUses { get; private set; } = [];
    [Output] public ITaskItem[] AuthoringReceipts { get; private set; } = [];

    private void ReadEvidence(JsonElement plan, string root)
    {
        var receipt = plan.GetProperty(Role + "Receipt");
        var fileName = Role == "app" ? SunderRpcAuthoringDocuments.AppAuthoringReceiptFileName : SunderRpcAuthoringDocuments.AuthoringReceiptFileName;
        if (receipt.GetProperty("path").GetString() != fileName) throw new InvalidDataException("The role receipt path is invalid.");
        var receiptPath = Path.Combine(root, fileName);
        GeneratedOutputPathSafety.EnsureSafeAncestors(receiptPath, includeLeaf: true, "package role receipt");
        var packageId = plan.GetProperty("packageId").GetString()!;
        var packageVersion = plan.GetProperty("packageVersion").GetString()!;
        var lockSha256 = plan.GetProperty("lockSha256").GetString()!;
        var receiptSha256 = receipt.GetProperty("sha256").GetString()!;
        var projection = SunderRpcAuthoringDocuments.ReadAuthoringReceiptProjection(receiptPath, receiptSha256, lockSha256, packageId, packageVersion);
        var snapshots = new List<ITaskItem>();
        var bundles = new List<ITaskItem>();
        foreach (var (key, descriptor) in projection.Contracts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var path = Path.Combine(root, GenerateSunderRpcBindingsTask.SnapshotRelativePath(descriptor));
            GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "package role descriptor");
            if (!SunderRpcDescriptorFileReader.ReadStableBytes(path, 1024 * 1024, "package role descriptor").AsSpan().SequenceEqual(descriptor.GetCanonicalUtf8()))
                throw new InvalidDataException("A package role descriptor changed after package analysis.");
            snapshots.Add(GenerateSunderRpcBindingsTask.SnapshotItem(path, path, descriptor, root, GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(root)));
            var manifest = projection.Manifest.ContractBundles!.Single(bundle => bundle!.ContractId == descriptor.ContractId)!;
            bundles.Add(Item(path, ("ContractId", descriptor.ContractId), ("Version", descriptor.Version), ("Exposure", manifest.Exposure!), ("DescriptorPath", $"contracts/{key}.rpc.json")));
        }
        var providers = (projection.Manifest.Provides ?? []).Select(provider => Item(provider!.ProviderId!,
            ("ContractId", provider.ContractId!), ("ContractVersion", provider.ContractVersion!), ("Role", provider.Role!), ("Audience", provider.Audience!))).ToArray();
        var uses = (projection.Manifest.UsesContracts ?? []).Select(use => Item(use!.ContractId!,
            ("ContractVersion", use.ContractVersion!), ("Role", use.Role!), ("Required", use.Required == true ? "true" : "false"), ("Actions", string.Join(';', use.Actions!)))).ToArray();
        var receipts = new[] { Item(receiptPath, ("Sha256", receiptSha256), ("LockSha256", lockSha256), ("PackageId", packageId),
            ("PackageVersion", packageVersion), ("GeneratedDirectory", root)) };
        BindingSnapshots = snapshots.ToArray(); ContractBundles = bundles.ToArray(); RpcProviders = providers; RpcUses = uses; AuthoringReceipts = receipts;

        static ITaskItem Item(string identity, params (string Name, string Value)[] metadata)
        {
            var item = new TaskItem(identity);
            foreach (var pair in metadata) item.SetMetadata(pair.Name, pair.Value);
            return item;
        }
    }
}
