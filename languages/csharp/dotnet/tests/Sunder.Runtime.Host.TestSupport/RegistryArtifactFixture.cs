using System.Net;
using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Host.TestSupport;

internal sealed record RegistryArtifactFixture(
    RegistryPackageInstallPlanItem PlanItem,
    IReadOnlyDictionary<string, byte[]> ContentByUrl)
{
    internal static async Task<RegistryArtifactFixture> CreateAsync(
        string root, string name, string origin, string packageId, string? currentVersion, string version,
        string entryAssemblySource, IReadOnlyList<string>? runtimeIdentifiers = null,
        IReadOnlyList<string>? runtimeRequiredHostCapabilities = null, IReadOnlyList<string>? roles = null)
    {
        RuntimeLocalState.EnsureInitialized(root);
        var artifactRoot = Path.Combine(root, "registry-artifacts", name);
        var sourceRoot = Path.Combine(artifactRoot, "source");
        CanonicalPackageTestBuilder.WriteExplodedPackage(sourceRoot, packageId, version, entryAssemblySource,
            runtimeIdentifiers: runtimeIdentifiers, runtimeRequiredHostCapabilities: runtimeRequiredHostCapabilities, roles: roles);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(sourceRoot);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        var sourceArchiveSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{packageId}:{version}:{name}")));
        var packageContentIdentity = PackageSessionPreparer.ComputeContentIdentity(sourceRoot);
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        var manifestRoles = (validation.Manifest!.Targets ?? []).Where(target => target?.Role is not null)
            .Select(target => target!.Role!).Distinct(StringComparer.Ordinal).ToArray();
        var keys = new List<SunderPackageProjectionKey> { SunderPackageProjectionKey.Shared };
        if (manifestRoles.Contains(SunderPackageFormat.RuntimeHostRole, StringComparer.Ordinal)) keys.Add(SunderPackageProjectionKey.Runtime(rid));
        if (manifestRoles.Contains(SunderPackageFormat.AppHostRole, StringComparer.Ordinal)) keys.Add(SunderPackageProjectionKey.App(rid));
        var artifacts = new List<RegistryPackageProjectionArtifact>(keys.Count);
        var contentByUrl = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var path = Path.Combine(artifactRoot, $"{key.Kind}-{key.Rid ?? "none"}.sunderprojection");
            var descriptor = await SunderPackageProjectionArchiveWriter.WriteAsync(sourceRoot, validation.Manifest!,
                validation.ContentIndex!, sourceArchiveSha256, key, path);
            var content = await File.ReadAllBytesAsync(path);
            var downloadUrl = new Uri(RegistryOrigin.Normalize(origin),
                $"artifacts/{Uri.EscapeDataString(name)}-{key.Kind}-{key.Rid ?? "none"}.sunderprojection").AbsoluteUri;
            contentByUrl.Add(downloadUrl, content);
            artifacts.Add(new(key.Kind, key.Rid, Convert.ToHexStringLower(SHA256.HashData(content)), content.LongLength,
                downloadUrl, descriptor.SourceArchiveSha256!, descriptor.ManifestSha256!, descriptor.ProjectionSha256!, descriptor.ProjectionFormatVersion));
        }
        return new(new(packageId, currentVersion, version, packageContentIdentity,
            currentVersion is null ? RegistryPackageActionKinds.PackageInstall : RegistryPackageActionKinds.PackageUpdate,
            null, [], validation.Manifest!.Targets!.Where(target => target is not null).Select(target => new RegistryPackageTarget(
                target!.Role!, target.Rid!, target.Kind!, target.EntryPoint!, target.TargetFramework, target.SdkVersion,
                (target.RequiredHostCapabilities ?? []).Select(capability => capability!).ToArray(),
                (target.Views ?? []).Where(view => view is not null).Select(view => new RegistryPackageWebView(
                    view!.ViewId!, view.DisplayName!, view.Route!, view.Icon, view.DefaultPlacement!, view.ShowInHotbar!.Value)).ToArray(),
                target.ExecutionRuntime is null ? null : new RegistryExecutionRuntimeRequirement(
                    target.ExecutionRuntime.Id!, target.ExecutionRuntime.VersionRange!, target.ExecutionRuntime.ModuleAbi!),
                target.AvaloniaVersion)).ToArray(), artifacts), contentByUrl);
    }

    public HttpResponseMessage CreateResponse(Uri uri)
        => TryCreateResponse(uri, out var response) ? response
            : throw new InvalidOperationException($"No test Registry artifact exists at '{uri}'.");

    public bool TryCreateResponse(Uri uri, out HttpResponseMessage response)
    {
        if (!ContentByUrl.TryGetValue(uri.AbsoluteUri, out var content))
        {
            response = null!;
            return false;
        }
        response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(content), RequestMessage = new(HttpMethod.Get, uri) };
        return true;
    }
}
