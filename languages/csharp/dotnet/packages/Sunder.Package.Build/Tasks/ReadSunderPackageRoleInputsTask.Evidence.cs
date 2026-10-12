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
        var allReceipts = new List<SunderRpcAuthoringReceiptProjection> { projection };
        foreach (var otherRole in new[] { "worker", "app" }.Where(value => value != Role))
        {
            var other = plan.GetProperty(otherRole + "Receipt");
            if (other.ValueKind == JsonValueKind.Null) continue;
            var name = otherRole == "app" ? SunderRpcAuthoringDocuments.AppAuthoringReceiptFileName : SunderRpcAuthoringDocuments.AuthoringReceiptFileName;
            if (other.GetProperty("path").GetString() != name) throw new InvalidDataException("The sibling role receipt path is invalid.");
            allReceipts.Add(SunderRpcAuthoringDocuments.ReadAuthoringReceiptProjection(Path.Combine(root, name),
                other.GetProperty("sha256").GetString()!, lockSha256, packageId, packageVersion));
        }
        var combined = SunderRpcAuthoringDocuments.MergeReceiptProjections(allReceipts);
        var projectionBytes = SunderRpcDescriptorFileReader.ReadStableBytes(Path.Combine(root, SunderRpcAuthoringDocuments.ModelProjectionLogicalPath),
            4_194_304, "package role public model projection");
        if (!projectionBytes.AsSpan().SequenceEqual(SunderRpcAuthoringDocuments.PublicModelProjectionBytes(combined)))
            throw new InvalidDataException("The public model projection differs from the complete role receipt union.");
        var snapshots = new List<ITaskItem>();
        var bundles = new List<ITaskItem>();
        foreach (var (key, descriptor) in projection.Contracts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var path = Path.Combine(root, GenerateSunderRpcBindingsTask.SnapshotRelativePath(descriptor));
            GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "package role descriptor");
            if (!SunderRpcDescriptorFileReader.ReadStableBytes(path, 4_194_304, "package role descriptor").AsSpan().SequenceEqual(descriptor.GetCanonicalUtf8()))
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

    private static void ValidateGeneratedSourceMembership(JsonElement plan, string root)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in new[] { "worker", "app" })
            foreach (var file in plan.GetProperty(role + "Compile").EnumerateArray())
                expected.Add(file.GetProperty("path").GetString()!);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(root);
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            GeneratedOutputPathSafety.EnsureRegularDirectory(directory, "generated role source membership");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 32768) throw new InvalidDataException("Generated role membership exceeds its file budget.");
                GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "generated role source membership");
                if (Directory.Exists(path)) pending.Push(path);
                else if (path.EndsWith(".cs", StringComparison.Ordinal)) actual.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
            }
        }
        if (!expected.SetEquals(actual)) throw new InvalidDataException("Generated model/source membership differs from the complete role plan.");
    }
}
