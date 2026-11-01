using System.Reflection;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Xunit;
using MSBuildTaskItem = Microsoft.Build.Utilities.TaskItem;
using static Sunder.Package.Build.Tests.PackageManifestFixture;

namespace Sunder.Package.Build.Tests;

internal static class PackageOutputFixture
{
    internal static string CreateCanonicalLeaf(string root, string role, string rid, string nativeContent)
    {
        var leafRoot = Path.Combine(root, role + "-" + rid + "-leaf");
        var targetDirectory = Path.Combine(leafRoot, "target");
        var devPackage = Path.Combine(leafRoot, "sunder-dev");
        Directory.CreateDirectory(targetDirectory);
        var assemblyPath = Path.Combine(targetDirectory, Path.GetFileName(Assembly.GetExecutingAssembly().Location));
        File.Copy(Assembly.GetExecutingAssembly().Location, assemblyPath);
        var nativePath = Path.Combine(targetDirectory, "runtimes", rid, "native", "fixture.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(nativePath)!);
        File.WriteAllText(nativePath, nativeContent);
        var manifestPath = Path.Combine(leafRoot, "sunder-package.json");
        var manifestEngine = new TestBuildEngine();
        var manifestTask = CreateTask(manifestPath, manifestEngine);
        var target = new MSBuildTaskItem(role + "/" + rid);
        target.SetMetadata("Role", role);
        target.SetMetadata("Rid", rid);
        manifestTask.PackageTargets = [target];
        MSBuildTaskItem[] workerFiles = [];
        if (role == SunderPackageFormat.RuntimeHostRole)
        {
            var executableName = CSharpWorkerModuleName();
            var executablePath = Path.Combine(targetDirectory, executableName);
            File.WriteAllText(executablePath, "worker");
            target.SetMetadata("Kind", SunderPackageFormat.WorkerTargetKind);
            ConfigureWorkerTask(manifestTask, rid, target);
            workerFiles = [.. CreateDotNetWorkerFiles(targetDirectory, rid, executablePath), new MSBuildTaskItem(nativePath)];
        }
        Assert.True(manifestTask.Execute(), string.Join(Environment.NewLine, manifestEngine.Errors));
        var emitEngine = new TestBuildEngine();
        Assert.True(new EmitSunderDevOutputTask
        {
            BuildEngine = emitEngine,
            ManifestPath = manifestPath,
            DevPackagePath = devPackage,
            DiscoveryRoot = leafRoot,
            TargetDirectory = targetDirectory,
            ManagedFiles = role == SunderPackageFormat.AppHostRole ? [new MSBuildTaskItem(assemblyPath)] : [],
            NativeRuntimeFiles = role == SunderPackageFormat.AppHostRole ? [new MSBuildTaskItem(nativePath)] : [],
            WorkerFiles = workerFiles,
        }.Execute(), string.Join(Environment.NewLine, emitEngine.Errors));
        return devPackage;
    }

    internal static string CreateRpcLeaf(
        string root,
        string role,
        string rid,
        bool required = true,
        string audience = "owner",
        string exposure = "public",
        bool includeUse = true,
        bool includeProvider = true)
    {
        var leafRoot = Path.Combine(root, $"rpc-{role}-{rid}-leaf");
        var targetDirectory = Path.Combine(leafRoot, "target");
        var devPackage = Path.Combine(leafRoot, "sunder-dev");
        Directory.CreateDirectory(targetDirectory);
        var assemblyPath = Path.Combine(targetDirectory, Path.GetFileName(Assembly.GetExecutingAssembly().Location));
        File.Copy(Assembly.GetExecutingAssembly().Location, assemblyPath);
        var descriptorPath = WriteMinimalV1Descriptor(leafRoot);
        var manifestPath = Path.Combine(leafRoot, "sunder-package.json");
        var manifestEngine = new TestBuildEngine();
        var manifestTask = CreateTask(manifestPath, manifestEngine);
        manifestTask.ProjectDirectory = leafRoot;
        var target = new MSBuildTaskItem(role + "/" + rid);
        manifestTask.PackageTargets = [target];
        MSBuildTaskItem[] workerFiles = [];
        if (role == SunderPackageFormat.RuntimeHostRole)
        {
            var executableName = CSharpWorkerModuleName();
            var executablePath = Path.Combine(targetDirectory, executableName);
            File.WriteAllText(executablePath, "worker");
            target.SetMetadata("Kind", SunderPackageFormat.WorkerTargetKind);
            ConfigureWorkerTask(manifestTask, rid, target);
            workerFiles = CreateDotNetWorkerFiles(targetDirectory, rid, executablePath);
        }
        Assert.True(manifestTask.Execute(), string.Join(Environment.NewLine, manifestEngine.Errors));
        // This is an archive/aggregation fixture, not retired descriptor-first
        // authoring. Construct its complete current canonical contract evidence.
        var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!;
        var descriptor = Sunder.Sdk.Rpc.SunderRpcContractDescriptor.Parse(File.ReadAllBytes(descriptorPath));
        manifest["contractBundles"] = new JsonArray(new JsonObject
        {
            ["contractId"] = descriptor.ContractId, ["version"] = descriptor.Version, ["descriptorPath"] = "contracts/minimal.rpc.json",
            ["descriptorSha256"] = descriptor.Sha256, ["abiSha256"] = descriptor.AbiSha256, ["exposure"] = exposure,
            ["source"] = "owned", ["authorityKind"] = "package", ["authorityId"] = manifest["id"]!.GetValue<string>(),
        });
        manifest["usesContracts"] = includeUse ? new JsonArray(new JsonObject
        {
            ["role"] = role, ["contractId"] = descriptor.ContractId, ["contractVersion"] = descriptor.Version,
            ["abiSha256"] = descriptor.AbiSha256, ["required"] = required, ["actions"] = new JsonArray("discover", "invoke"),
        }) : new JsonArray();
        manifest["provides"] = includeProvider ? new JsonArray(new JsonObject
        {
            ["providerId"] = "test.rpc.provider", ["role"] = role, ["audience"] = audience,
            ["contractId"] = descriptor.ContractId, ["contractVersion"] = descriptor.Version,
            ["descriptorSha256"] = descriptor.Sha256, ["abiSha256"] = descriptor.AbiSha256,
        }) : new JsonArray();
        if (includeUse || includeProvider)
            foreach (var targetValue in manifest["targets"]!.AsArray())
            {
                var capabilities = targetValue!["requiredHostCapabilities"]!.AsArray();
                if (!capabilities.Any(value => value!.GetValue<string>() == SunderPackageFormat.RpcV1Capability)) capabilities.Add(SunderPackageFormat.RpcV1Capability);
            }
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var projection = new SunderRpcModelProjectionDocument(1, manifest["id"]!.GetValue<string>(), manifest["version"]!.GetValue<string>(), exposure == "public"
            ? [new(descriptor.ContractId, descriptor.Version, descriptor.Sha256, descriptor.AbiSha256, SunderRpcModelProjection.Empty)] : []);
        var projectionPath = Path.Combine(leafRoot, SunderPackageFormat.RpcModelProjectionFileName);
        File.WriteAllBytes(projectionPath, projection.ToCanonicalUtf8());
        var projectionItem = new MSBuildTaskItem(projectionPath); projectionItem.SetMetadata("DescriptorPath", SunderPackageFormat.RpcModelProjectionLogicalPath);
        var descriptorItem = new MSBuildTaskItem(descriptorPath); descriptorItem.SetMetadata("DescriptorPath", "contracts/minimal.rpc.json");
        var emitEngine = new TestBuildEngine();
        Assert.True(new EmitSunderDevOutputTask
        {
            BuildEngine = emitEngine,
            ManifestPath = manifestPath,
            DevPackagePath = devPackage,
            DiscoveryRoot = leafRoot,
            TargetDirectory = targetDirectory,
            ManagedFiles = role == SunderPackageFormat.AppHostRole ? [new MSBuildTaskItem(assemblyPath)] : [],
            WorkerFiles = workerFiles,
            ContractFiles = [descriptorItem, projectionItem],
        }.Execute(), string.Join(Environment.NewLine, emitEngine.Errors));
        return devPackage;
    }

    internal static MSBuildTaskItem TargetLeafItem(string path, string? discoveryRoot = null)
    {
        var item = new MSBuildTaskItem(path);
        item.SetMetadata("DiscoveryRoot", discoveryRoot ?? Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Target leaf '{path}' has no discovery root."));
        return item;
    }

    internal static string WriteMinimalV1Descriptor(string root, string version = "2.0.0")
    {
        var path = Path.Combine(root, "minimal.rpc.json");
        File.WriteAllText(
            path,
            MinimalV1Descriptor.Replace(
                "\"version\": \"2.0.0\"",
                $"\"version\": \"{version}\"",
                StringComparison.Ordinal));
        return path;
    }

    internal static MSBuildTaskItem ContractBundle(
        string descriptorPath,
        string exposure = "public",
        string version = "2.0.0")
    {
        var item = new MSBuildTaskItem(descriptorPath);
        item.SetMetadata("ContractId", "example.rpc.v1.minimal");
        item.SetMetadata("Version", version);
        item.SetMetadata("DescriptorPath", "contracts/minimal.rpc.json");
        item.SetMetadata("Exposure", exposure);
        return item;
    }

    internal static MSBuildTaskItem ContractUse(
        string role,
        string version = "2.0.0",
        bool required = true)
    {
        var item = new MSBuildTaskItem("example.rpc.v1.minimal");
        item.SetMetadata("Role", role);
        item.SetMetadata("ContractVersion", version);
        item.SetMetadata("Required", required ? "true" : "false");
        item.SetMetadata("Actions", "discover;invoke");
        return item;
    }

    internal static MSBuildTaskItem RpcProvider(
        string role,
        string version = "2.0.0",
        string audience = "owner")
    {
        var item = new MSBuildTaskItem("test.rpc.provider");
        item.SetMetadata("Role", role);
        item.SetMetadata("ContractId", "example.rpc.v1.minimal");
        item.SetMetadata("ContractVersion", version);
        item.SetMetadata("Audience", audience);
        return item;
    }
}
