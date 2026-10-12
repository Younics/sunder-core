using Sunder.App.Services;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Tests.TestSupport;

internal class RuntimeManagementTestClient : IRuntimeExecutionRuntimeManagementClient
{
    internal const string Origin = "https://registry.example/";
    internal static readonly string Hash = new('a', 64);
    internal static readonly RuntimeExecutionRuntimeRequirement Requirement = new("sunder.runtime.dotnet", ">=10.0.0 <11.0.0", "sunder.dotnet-worker-module.v1");
    internal static readonly RuntimeExecutionRuntimeIdentity Identity = new(ConfiguredOfficialSourceId.DotNet, Requirement.RuntimeId,
        "10.0.1", "osx-arm64", Requirement.ModuleAbi, RuntimeExecutionRuntimeChecksumAlgorithm.Sha512, new('a', 128));
    internal static RuntimeExecutionRuntimePlanIdentity Plan(string id = "install", char fingerprint = 'b') => new(id, new(fingerprint, 64), DateTimeOffset.UtcNow.AddMinutes(5), Identity.Rid);
    internal static RuntimeExecutionRuntimeDistribution Distribution() => new("https://builds.dotnet.microsoft.com/dotnet/runtime.tar.gz",
        "https://builds.dotnet.microsoft.com/dotnet/releases.json", Hash, null, null, RuntimeExecutionRuntimeArchiveFormat.TarGzip, "dotnet-runtime-v1", 1024);
    internal static RuntimeExecutionRuntimeSupportStatus Support() => new(RuntimeExecutionRuntimeSupportState.Supported, "10", DateTimeOffset.UtcNow, null, null);
    internal static RuntimeExecutionRuntimeInstalledArtifact InstalledArtifact() => new(Hash, 1024, Hash, new(Distribution(), Distribution().DownloadUrl, DateTimeOffset.UtcNow));
    internal static RuntimeExecutionRuntimeResolution Resolution() => new(Requirement, new(Identity, Distribution(), Support()),
        Hash, DateTimeOffset.UtcNow.AddMinutes(5), false, 1024);
    internal static RuntimeExecutionRuntimeInstallPlan InstallPlan(bool retain = true) => new(true, null, "Install .NET", Plan(), Resolution(), retain);
    internal static RuntimePackageRuntimePrerequisitePlan Prerequisites(bool ready, string? source = null) => new(true, ready, null,
        ready ? "Ready" : "Runtime required", source ?? Hash, Identity.Rid,
        [new(new("demo", "1.0.0", Hash, "runtime", Identity.Rid), Hash, Requirement, ready, ready ? null : "execution-runtime.missing",
            ready ? Identity : null, ready ? null : InstallPlan())], []);
    internal static InstalledExecutionRuntimeSummary Installed(int references = 0) => new(Identity, "ready", 2048, references, InstalledArtifact(),
        new("ready", DateTimeOffset.UtcNow, false, null), Support(), DateTimeOffset.UtcNow)
        { RetainedUntilUninstall = true };
    internal static RuntimeExecutionRuntimeUsage Usage(int references = 0, char fingerprint = 'c') => new(new(fingerprint, 64),
        references == 0 ? [] : [new("demo", "1.0.0", Hash, "runtime", Identity.Rid)],
        ["demo"], ["demo"], references, 0, 0, 1, false);
    internal static RuntimeExecutionRuntimeUpdatePlan UpdatePlan(string? prepared = null, char fingerprint = 'c') => new(true, null, "Update ready", Plan("update", fingerprint), prepared,
        [new(Identity, Resolution(), true)], [], Usage(fingerprint: fingerprint));
    internal static RuntimeExecutionRuntimePreparedUpdate Prepared(RuntimeExecutionRuntimeUpdateState state = RuntimeExecutionRuntimeUpdateState.Deferred)
        => new("prepared-1", Hash, state, DateTimeOffset.UtcNow, [new(Identity, Resolution(), true)], []);

    public List<string> Calls { get; } = [];
    public RuntimeExecutionRuntimeSettings Settings { get; set; } = new(1);
    public List<RuntimeExecutionRuntimeSettingsUpdateRequest> SettingsWrites { get; } = [];
    public bool FailSettingsWrite { get; set; }
    public IReadOnlyList<InstalledExecutionRuntimeSummary> Inventory { get; set; } = [Installed()];
    public RuntimeExecutionRuntimeUpdateStatus Status { get; set; } = new(false, null, null, null, []);
    public Func<RuntimePackageRuntimePrerequisiteRequest, CancellationToken, Task<RuntimePackageRuntimePrerequisitePlan>> PreviewPrerequisites { get; set; }
        = (_, _) => Task.FromResult(Prerequisites(true));
    public Func<RuntimeExecutionRuntimePlanConfirmation, CancellationToken, Task<RuntimeExecutionRuntimeOperationResult>>? InstallHandler { get; set; }
    public Func<RuntimeExecutionRuntimeUpdatePreviewRequest, RuntimeExecutionRuntimeUpdatePlan> UpdatePreview { get; set; }
        = request => UpdatePlan(request.PreparedUpdateId);
    public Func<RuntimeExecutionRuntimeUpdateApplyRequest, RuntimeExecutionRuntimeUpdateResult>? ApplyResult { get; set; }
    public Func<CancellationToken, Task<RuntimeExecutionRuntimeSettings>>? LoadSettings { get; set; }
    public RuntimeExecutionRuntimePlanConfirmation? InstallConfirmation { get; private set; }
    public RuntimeExecutionRuntimeUpdateApplyRequest? UpdateApply { get; private set; }
    public bool? ImportRetention { get; private set; }
    public List<RuntimeExecutionRuntimeInstallPreviewRequest> InstallPreviews { get; } = [];
    public List<RuntimeExecutionRuntimeCatalogRequest> CatalogRequests { get; } = [];
    public Func<RuntimeExecutionRuntimeCatalogRequest, CancellationToken, Task<RuntimeExecutionRuntimeCatalogPage>>? LoadCatalog { get; set; }
    public Func<CancellationToken, Task<IReadOnlyList<InstalledExecutionRuntimeSummary>>>? LoadInventory { get; set; }

    public Task<RuntimeExecutionRuntimeSettings> GetExecutionRuntimeSettingsAsync(CancellationToken cancellationToken = default)
    { Calls.Add("settings"); return LoadSettings?.Invoke(cancellationToken) ?? Task.FromResult(Settings); }
    public Task<RuntimeExecutionRuntimeSettings> UpdateExecutionRuntimeSettingsAsync(RuntimeExecutionRuntimeSettingsUpdateRequest request, CancellationToken cancellationToken = default)
    {
        SettingsWrites.Add(request);
        if (FailSettingsWrite || request.ExpectedRevision != Settings.Revision) throw new InvalidOperationException("Settings revision changed.");
        Settings = new(Settings.Revision + 1, request.AutomaticUpdatesEnabled);
        return Task.FromResult(Settings);
    }
    public Task<IReadOnlyList<InstalledExecutionRuntimeSummary>> GetExecutionRuntimesAsync(CancellationToken cancellationToken = default)
    { Calls.Add("inventory"); return LoadInventory?.Invoke(cancellationToken) ?? Task.FromResult(Inventory); }
    public Task<RuntimeExecutionRuntimeUpdateStatus> GetExecutionRuntimeUpdateStatusAsync(CancellationToken cancellationToken = default)
    { Calls.Add("status"); return Task.FromResult(Status); }
    public Task<RuntimeExecutionRuntimeCatalogPage> GetExecutionRuntimeCatalogAsync(RuntimeExecutionRuntimeCatalogRequest request, CancellationToken cancellationToken = default)
    {
        CatalogRequests.Add(request);
        return LoadCatalog?.Invoke(request, cancellationToken) ?? Task.FromResult(new RuntimeExecutionRuntimeCatalogPage(Identity.Rid, request.Skip, request.Take,
            request.SourceId == ConfiguredOfficialSourceId.Node ? 0 : 1,
            request.SourceId == ConfiguredOfficialSourceId.Node ? [] : [new(".NET", Resolution().Artifact, false)]));
    }
    public Task<IReadOnlyList<RuntimeExecutionRuntimePackageReference>> GetExecutionRuntimeReferencesAsync(RuntimeExecutionRuntimeIdentity identity, CancellationToken cancellationToken = default)
        => Task.FromResult(Usage().PackageReferences);
    public Task<RuntimePackageRuntimePrerequisitePlan> PreviewPackageRuntimePrerequisitesAsync(RuntimePackageRuntimePrerequisiteRequest request, CancellationToken cancellationToken = default)
    { Calls.Add("prerequisites"); return PreviewPrerequisites(request, cancellationToken); }
    public Task<RuntimeExecutionRuntimeInstallPlan> PreviewExecutionRuntimeInstallAsync(RuntimeExecutionRuntimeInstallPreviewRequest request, CancellationToken cancellationToken = default)
    { InstallPreviews.Add(request); return Task.FromResult(InstallPlan(request.RetainUntilUninstall)); }
    public Task<RuntimeExecutionRuntimeOperationResult> ApplyExecutionRuntimeInstallAsync(RuntimeExecutionRuntimePlanConfirmation request, CancellationToken cancellationToken = default)
    {
        Calls.Add("runtime-install"); InstallConfirmation = request;
        return InstallHandler?.Invoke(request, cancellationToken) ?? Task.FromResult(new RuntimeExecutionRuntimeOperationResult(true, null, "Installed", "install", Installed(), false));
    }
    public Task<RuntimeExecutionRuntimeOperationResult> ImportExecutionRuntimeAsync(string filePath, bool retainUntilUninstall, CancellationToken cancellationToken = default)
    { ImportRetention = retainUntilUninstall; return Task.FromResult(new RuntimeExecutionRuntimeOperationResult(true, null, "Imported", "import", Installed(), false)); }
    public Task<RuntimeExecutionRuntimeOperationResult> VerifyExecutionRuntimeAsync(RuntimeExecutionRuntimeIdentity identity, CancellationToken cancellationToken = default)
        => Task.FromResult(new RuntimeExecutionRuntimeOperationResult(true, null, "Verified", "verify", Installed(), true));
    public Task<RuntimeExecutionRuntimeImpact> GetExecutionRuntimeImpactAsync(RuntimeExecutionRuntimeIdentity identity, CancellationToken cancellationToken = default)
        => Task.FromResult(new RuntimeExecutionRuntimeImpact(1, ["demo"], Hash));
    public Task<RuntimeExecutionRuntimeOperationResult> RepairExecutionRuntimeAsync(RuntimeExecutionRuntimeIdentity identity, RuntimeExecutionRuntimeImpact impact, CancellationToken cancellationToken = default)
        => Task.FromResult(new RuntimeExecutionRuntimeOperationResult(true, null, "Repaired", "repair", Installed(), false));
    public Task<RuntimeExecutionRuntimeUpdatePlan> PreviewExecutionRuntimeUpdateAsync(RuntimeExecutionRuntimeUpdatePreviewRequest request, CancellationToken cancellationToken = default)
    { Calls.Add(request.PreparedUpdateId is null ? "preview-update" : "preview-prepared"); return Task.FromResult(UpdatePreview(request)); }
    public Task<RuntimeExecutionRuntimeUpdateResult> PrepareExecutionRuntimeUpdateAsync(RuntimeExecutionRuntimePlanConfirmation request, CancellationToken cancellationToken = default)
    { Calls.Add("prepare"); Status = new(false, null, null, null, [Prepared()]); return Task.FromResult(new RuntimeExecutionRuntimeUpdateResult(true, null, "Downloaded", RuntimeExecutionRuntimeUpdateState.Prepared, Prepared())); }
    public Task<RuntimeExecutionRuntimeUpdateResult> ApplyExecutionRuntimeUpdateAsync(RuntimeExecutionRuntimeUpdateApplyRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add("apply-update"); UpdateApply = request;
        if (ApplyResult is not null) return Task.FromResult(ApplyResult(request));
        var restart = request.Mode == RuntimeExecutionRuntimeApplyMode.RestartAffectedPackages;
        return Task.FromResult(new RuntimeExecutionRuntimeUpdateResult(true, null, "Waiting", restart ? RuntimeExecutionRuntimeUpdateState.Applied : RuntimeExecutionRuntimeUpdateState.Deferred,
            Prepared(), StoreCommitted: restart, RuntimeSessionApplied: restart));
    }
    public Task<RuntimeExecutionRuntimeUninstallPlan> PreviewExecutionRuntimeUninstallAsync(RuntimeExecutionRuntimeUninstallPreviewRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add("remove-preview");
        var count = Inventory.FirstOrDefault(item => item.Identity == request.Runtime)?.PackageReferenceCount ?? 0;
        return Task.FromResult(new RuntimeExecutionRuntimeUninstallPlan(true, null, count == 0 ? "Unused" : "Still used by demo", Plan("remove"), request.Runtime, Usage(count), count == 0));
    }
    public Task<RuntimeExecutionRuntimeUninstallResult> ApplyExecutionRuntimeUninstallAsync(RuntimeExecutionRuntimePlanConfirmation request, CancellationToken cancellationToken = default)
    { Calls.Add("remove"); return Task.FromResult(new RuntimeExecutionRuntimeUninstallResult(true, null, "Removed", Identity, true, false)); }
    public void Dispose() { }
}
