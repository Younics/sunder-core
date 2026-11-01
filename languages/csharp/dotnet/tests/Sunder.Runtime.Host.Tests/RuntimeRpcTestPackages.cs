using System.Text.Json;
using System.Text;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using static Sunder.Runtime.Host.Tests.RuntimeRpcBrokerFixture;

namespace Sunder.Runtime.Host.Tests;

internal static class RuntimeRpcTestPackages
{
    internal static ActiveLoadedPackage AddWebTarget(
        ActiveLoadedPackage package,
        SunderRpcContractDescriptor descriptor,
        bool usesContract)
    {
        var root = package.Source.SourceFolder;
        var manifestFolder = Path.Combine(root, "manifest");
        var contractFolder = Path.Combine(root, "payload", "shared", "contracts");
        Directory.CreateDirectory(manifestFolder);
        Directory.CreateDirectory(contractFolder);
        var descriptorBytes = descriptor.GetCanonicalUtf8();
        File.WriteAllBytes(Path.Combine(contractFolder, "rpc.json"), descriptorBytes);
        var manifest = new SunderPackageManifest
        {
            ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
            ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
            Id = package.Source.Manifest!.Id,
            Name = package.Source.Manifest.Id,
            Version = package.Source.Manifest.Version,
            ContractBundles = package.Source.Manifest.ContractBundles,
            UsesContracts = usesContract
                ? (package.Source.Manifest.UsesContracts ?? [])
                    .Concat(
                    [
                        new SunderPackageContractUseManifest
                        {
                            Role = SunderPackageFormat.AppHostRole,
                            ContractId = descriptor.ContractId,
                            ContractVersion = descriptor.Version,
                            AbiSha256 = descriptor.AbiSha256,
                            Required = true,
                            Actions = ["discover", "invoke", "subscribe"],
                        },
                    ])
                    .ToArray()
                : package.Source.Manifest.UsesContracts,
            Provides = package.Source.Manifest.Provides,
            Targets =
            [
                new SunderPackageTargetManifest
                {
                    Role = SunderPackageFormat.AppHostRole,
                    Rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                    Kind = SunderPackageFormat.WebTargetKind,
                    EntryPoint = "index.html",
                    SdkVersion = "1.1.0",
                    RequiredHostCapabilities = ["sdk-baseline-1-1.v1", "rpc.v1"],
                    Views =
                    [
                        new SunderPackageWebViewManifest
                        {
                            ViewId = package.Source.Manifest!.Id + ".main",
                            DisplayName = "Caller",
                            Route = "/",
                            DefaultPlacement = "middle",
                            ShowInHotbar = true,
                        },
                    ],
                },
            ],
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        File.WriteAllBytes(Path.Combine(manifestFolder, "sunder-package.json"), manifestBytes);
        var contentIndex = new SunderPackageContentIndex(
            SunderPackageFormat.CurrentContentIndexVersion,
            [
                new SunderPackageContentIndexEntry(
                    SunderPackageFormat.ManifestPath,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(manifestBytes)).ToLowerInvariant(),
                    manifestBytes.Length),
                new SunderPackageContentIndexEntry(
                    SunderPackageFormat.SharedPayloadRoot + "contracts/rpc.json",
                    descriptor.Sha256,
                    descriptorBytes.Length),
            ]);
        return package with
        {
            Source = package.Source with
            {
                HostRoles = PackageHostRoles.App | PackageHostRoles.Runtime,
                Manifest = manifest,
                ContentIndex = contentIndex,
            },
        };
    }

    internal static ActiveLoadedPackage CreatePackage(
        string packageId,
        SunderRpcContractDescriptor descriptor,
        TestHandler? handler,
        bool usesContract,
        PackageSourceKind sourceKind = PackageSourceKind.Installed,
        TestHandler? siblingHandler = null,
        string providerId = "example.provider",
        string providerAudience = SunderPackageFormat.PublicProviderAudience,
        IReadOnlyList<string>? requiredHostCapabilities = null)
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        var selectedTarget = requiredHostCapabilities is null
            ? null
            : new SunderPackageTargetManifest
            {
                Role = SunderPackageFormat.RuntimeHostRole,
                Rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Kind = SunderPackageFormat.WorkerTargetKind,
                EntryPoint = "lib/test",
                SdkVersion = "1.1.0",
                RequiredHostCapabilities = requiredHostCapabilities.Cast<string?>().ToArray(),
            };
        var manifest = new SunderPackageManifest
        {
            Id = packageId,
            Version = "1.0.0",
            ContractBundles =
            [
                new SunderPackageContractBundleManifest
                {
                    Source = "owned",
                    AuthorityKind = "package",
                    AuthorityId = packageId,
                    ContractId = descriptor.ContractId,
                    Version = descriptor.Version,
                    DescriptorPath = "contracts/rpc.json",
                    DescriptorSha256 = descriptor.Sha256,
                    AbiSha256 = descriptor.AbiSha256,
                    Exposure = SunderPackageFormat.PublicContractExposure,
                },
            ],
            UsesContracts = usesContract
                ?
                [
                    new SunderPackageContractUseManifest
                    {
                        Role = SunderPackageFormat.RuntimeHostRole,
                        ContractId = descriptor.ContractId,
                        ContractVersion = descriptor.Version,
                        AbiSha256 = descriptor.AbiSha256,
                        Required = true,
                        Actions = ["discover", "invoke", "subscribe"],
                    },
                ]
                : [],
            Provides = handler is null
                ? []
                : siblingHandler is null
                    ? [ProviderManifest(descriptor, providerId, providerAudience)]
                    :
                    [
                        ProviderManifest(descriptor, providerId, providerAudience),
                        ProviderManifest(descriptor, "example.sibling", providerAudience),
                    ],
            Targets = selectedTarget is null ? [] : [selectedTarget],
        };
        var source = new RuntimePackageSource(
            packageId,
            sourceKind,
            root,
            Manifest: manifest,
            SelectedTarget: selectedTarget);
        var package = new ActiveLoadedPackage(
            new ActivePackageDescriptor(
                packageId,
                packageId,
                "1.0.0",
                PackageHostRoles.Runtime,
                null,
                true,
                PackageReadinessState.Ready,
                []),
            source,
            SettingsSchema: null,
            new JsonPackageKeyValueStore(Path.Combine(root, "state.json")),
            new JsonPackageSecretsStore(
                Path.Combine(root, "secrets.json"),
                null,
                null,
                new RestrictedFileMasterKeyProtection()),
            new LocalPackageFileStore(Path.Combine(root, "files")),
            EmptyTestPackageSettings.Instance,
            new TestRuntimePackageWorker())
        {
            RpcContracts = new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal)
            {
                [PackageSessionPreparer.ContractKey(descriptor.ContractId, descriptor.Version)] = descriptor,
            },
            RpcContractUses = (manifest.UsesContracts ?? []).Select(static use => use!).ToArray(),
            RpcManifestSha256 = new string('a', 64),
        };
        if (handler is not null)
        {
            var handlers = new Dictionary<string, TestHandler>(StringComparer.Ordinal)
            {
                [providerId] = handler,
            };
            if (siblingHandler is not null) handlers.Add("example.sibling", siblingHandler);
            package = package with
            {
                RpcProviders = manifest.Provides!.Select(static declaration => declaration!)
                    .ToDictionary(
                        static declaration => declaration.ProviderId!,
                        declaration => new RuntimeRpcProviderRegistration(
                            declaration.ProviderId!,
                            descriptor.ContractId,
                            descriptor.Version,
                            descriptor.Sha256,
                            descriptor,
                            handlers[declaration.ProviderId!],
                            declaration),
                        StringComparer.Ordinal),
            };
        }
        return package;
    }

    internal static ActivePackageSession CreateSession(params ActiveLoadedPackage[] packages)
        => new(
            sessionFolder: null,
            packages.ToDictionary(static package => package.Descriptor.PackageId, StringComparer.OrdinalIgnoreCase),
            packages.ToDictionary(
                static package => package.Descriptor.PackageId,
                static package => new SessionPackageDescriptor(
                    package.Descriptor.PackageId,
                    package.Descriptor.PackageId,
                    package.Descriptor.Version,
                    PackageHostRoles.Runtime,
                    null,
                    true,
                    PackageReadinessState.Ready,
                    [],
                     null,
                     null,
                     null,
                     0,
                     package.RpcContractUses.Select(use => new PackageRpcContractUseDescriptor(
                         use.Role!,
                         use.ContractId!,
                         use.ContractVersion!,
                         use.AbiSha256!,
                         use.Required == true,
                         (use.Actions ?? []).Where(static action => action is not null).Select(static action => action!).ToArray()))
                         .ToArray()),
                 StringComparer.OrdinalIgnoreCase));

    internal static SunderPackageProviderManifest ProviderManifest(
        SunderRpcContractDescriptor descriptor,
        string providerId = "example.provider",
        string audience = SunderPackageFormat.PublicProviderAudience)
        => new()
        {
            ProviderId = providerId,
            ContractId = descriptor.ContractId,
            ContractVersion = descriptor.Version,
            DescriptorSha256 = descriptor.Sha256,
            AbiSha256 = descriptor.AbiSha256,
            Role = SunderPackageFormat.RuntimeHostRole,
            Audience = audience,
        };

    internal static SunderRpcContractDescriptor Contract(
        string version = "1.0.0",
        int requestMaximumLength = 128,
        string? requestTitle = null,
        string unaryIdempotency = "none",
        bool declareErrorDetails = false)
    {
        var title = requestTitle is null
            ? string.Empty
            : $",\"title\":{JsonSerializer.Serialize(requestTitle)}";
        var errorDetails = declareErrorDetails
            ? ",\"detailsSchema\":{\"$ref\":\"#/$defs/ErrorDetails\"}"
            : string.Empty;
        var errorDetailsDefinition = declareErrorDetails
            ? ",\"ErrorDetails\":{\"type\":\"object\",\"properties\":{\"reason\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}},\"required\":[\"reason\"],\"additionalProperties\":false}"
            : string.Empty;
        return SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes($$$$"""
            {
              "$schema":"https://json-schema.org/draft/2020-12/schema",
              "descriptorVersion":1,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"example.rpc",
              "version":"{{{{version}}}}",
              "errors":{"messages.rejected":{"description":"The message was rejected."{{{{errorDetails}}}}}},
              "services":{"messages":{"methods":{
                "send":{"kind":"unary","idempotency":"{{{{unaryIdempotency}}}}","errors":["messages.rejected"],"requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Response"}},
                "watch":{"kind":"server-stream","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Request"},"eventSchema":{"$ref":"#/$defs/Response"}}
              }}},
              "$defs":{
                "Request":{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":{{{{requestMaximumLength}}}}}},"required":["message"],"additionalProperties":false{{{{title}}}}},
                "Response":{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false}{{{{errorDetailsDefinition}}}}
              }
            }
            """));
    }
}
