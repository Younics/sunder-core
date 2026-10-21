using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

public sealed partial class SettingsPackageRuntimesViewModel : ViewModelBase, IDisposable
{
    private readonly IRuntimeExecutionRuntimeManagementClient _client;
    private readonly Func<RuntimeConfirmationViewModel, CancellationToken, Task<bool>> _confirm;
    private readonly BackgroundProcessQueueService _queue;
    private readonly Func<IRuntimeExecutionRuntimeManagementClient> _createOperationClient;
    private readonly IRuntimeExecutionRuntimeStatusSource? _changes;
    private readonly IUiDispatcher _dispatcher;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly Func<RuntimePackageStamp, CancellationToken, Task<PackagePresentationResult>>? _waitForPresentation;
    private readonly OwnedTaskObserver _tasks = new(nameof(SettingsPackageRuntimesViewModel));
    private CancellationTokenSource? _page;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _operationCompletion;
    private RuntimeExecutionRuntimeSettings? _settings;
    private bool _loadingPreference;
    private bool _disposed;
    private bool _inventoryLoaded;
    private DateTimeOffset? _lastManualCheck;
    private RuntimeExecutionRuntimeUpdateStatus? _updateStatus;
    private readonly Dictionary<ConfiguredOfficialSourceId, string> _catalogErrors = [];

    internal SettingsPackageRuntimesViewModel(
        IRuntimeExecutionRuntimeManagementClient client,
        Func<RuntimeConfirmationViewModel, CancellationToken, Task<bool>> confirm,
        BackgroundProcessQueueService queue,
        Func<IRuntimeExecutionRuntimeManagementClient> createOperationClient,
        Func<RuntimePackageStamp, CancellationToken, Task<PackagePresentationResult>>? waitForPresentation = null,
        IRuntimeExecutionRuntimeStatusSource? changes = null,
        IUiDispatcher? uiDispatcher = null)
    {
        _client = client;
        _confirm = confirm;
        _queue = queue;
        _createOperationClient = createOperationClient;
        _changes = changes;
        _dispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
        _waitForPresentation = waitForPresentation;
    }

    public ObservableCollection<InstalledPackageRuntimeItem> Installed { get; } = [];
    public ObservableCollection<AvailablePackageRuntimeItem> Available { get; } = [];
    public ObservableCollection<PreparedPackageRuntimeItem> Prepared { get; } = [];
    public IReadOnlyList<PackageRuntimeFamilyViewModel> Families { get; } =
    [
        new(ConfiguredOfficialSourceId.DotNet, ".NET", "For packages built with C# and .NET."),
        new(ConfiguredOfficialSourceId.Node, "Node.js", "For packages built with JavaScript and TypeScript."),
    ];
    public bool HasInstalled => Installed.Count > 0;
    public bool HasAvailable => Available.Count > 0;
    public bool HasPrepared => Prepared.Count > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);
    public bool CanAct => _page is not null && !IsBusy;
    public bool CanChangePreference => CanAct && _settings is not null;

    [ObservableProperty] private bool _automaticUpdatesEnabled = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Open this page to check package runtimes.";
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private string _deviceText = string.Empty;
    [ObservableProperty] private string _lastCheckedText = "Not checked yet";

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnIsBusyChanged(bool value) => NotifyActions();
    partial void OnAutomaticUpdatesEnabledChanged(bool value)
    {
        if (!_loadingPreference && _settings is not null)
            _tasks.Observe(SavePreferenceAsync(value), "saving package runtime update preference");
    }

    public async Task ActivateAsync()
    {
        if (_disposed || _page is not null) return;
        _page = new();
        if (_changes is not null) _changes.ExecutionRuntimesChanged += OnRuntimeStatusChanged;
        _queue.ProcessChanged += OnQueuedProcessChanged;
        RefreshQueuedOperations();
        var token = _page.Token;
        NotifyActions();
        if (_operationCompletion is { } finishing)
        {
            try { await finishing.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
        if (token.IsCancellationRequested) return;
        await RefreshAsync();
    }

    public void Deactivate()
    {
        var page = _page;
        _page = null;
        if (_changes is not null) _changes.ExecutionRuntimesChanged -= OnRuntimeStatusChanged;
        _queue.ProcessChanged -= OnQueuedProcessChanged;
        _refreshRequested = false;
        _refreshCancellation?.Cancel();
        page?.Cancel();
        _operation?.Cancel();
        page?.Dispose();
        NotifyActions();
    }

    [RelayCommand]
    public Task RefreshAsync() => RunAsync("Loading package runtimes…", async token =>
    {
        var settings = await _client.GetExecutionRuntimeSettingsAsync(token);
        token.ThrowIfCancellationRequested();
        ApplySettings(settings);
        await RefreshInventoryAndStatusAsync(token);
        await LoadCatalogAsync(token);
        StatusText = string.Empty;
    });

    public async Task SavePreferenceAsync(bool enabled)
    {
        var previous = _settings;
        var page = _page;
        if (previous is null) return;
        var saved = await RunAsync("Saving automatic runtime updates…", async token =>
        {
            var settings = await _client.UpdateExecutionRuntimeSettingsAsync(new(previous.Revision, enabled), token);
            token.ThrowIfCancellationRequested();
            ApplySettings(settings);
            StatusText = settings.AutomaticUpdatesEnabled
                ? "Automatic runtime updates are on. Running packages wait until stopped."
                : "Automatic runtime updates are off. Manual updates and security checks remain available.";
        });
        if (!saved && ReferenceEquals(_page, page))
        {
            ApplySettings(previous);
            if (_page is not null) ErrorText = "The preference change was not confirmed. Refresh to read the current Host setting before trying again. " + ErrorText;
        }
    }

    private void ApplySettings(RuntimeExecutionRuntimeSettings settings)
    {
        _settings = settings;
        _loadingPreference = true;
        try { AutomaticUpdatesEnabled = settings.AutomaticUpdatesEnabled; }
        finally { _loadingPreference = false; }
        NotifyActions();
    }

    private async Task RefreshInventoryAndStatusAsync(CancellationToken token)
    {
        var installed = await _client.GetExecutionRuntimesAsync(token);
        token.ThrowIfCancellationRequested();
        var rows = new List<InstalledPackageRuntimeItem>();
        foreach (var runtime in installed)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var usage = await _client.PreviewExecutionRuntimeUninstallAsync(new(runtime.Identity), token);
                token.ThrowIfCancellationRequested();
                rows.Add(new(runtime, usage));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { rows.Add(new(runtime, null, error.Message)); }
        }
        token.ThrowIfCancellationRequested();
        Installed.ReplaceWith(rows);
        _inventoryLoaded = true;
        OnPropertyChanged(nameof(HasInstalled));
        await RefreshUpdateStatusAsync(token);
    }

    private async Task RefreshUpdateStatusAsync(CancellationToken token)
    {
        var status = await _client.GetExecutionRuntimeUpdateStatusAsync(token);
        token.ThrowIfCancellationRequested();
        _updateStatus = status;
        Prepared.ReplaceWith(status.PreparedUpdates.Select(update => new PreparedPackageRuntimeItem(update)));
        OnPropertyChanged(nameof(HasPrepared));
        RefreshLastCheckedText();
        RefreshFamilies();
    }

    private async Task LoadCatalogAsync(CancellationToken token)
    {
        var latest = new List<AvailablePackageRuntimeItem>();
        foreach (var family in Families)
        {
            try
            {
                var catalog = await _client.GetExecutionRuntimeCatalogAsync(new(SourceId: family.Source, Take: 1), token);
                token.ThrowIfCancellationRequested();
                DeviceText = catalog.Rid;
                latest.AddRange(catalog.Items.Where(item => item.Artifact.Identity.SourceId == family.Source)
                    .Take(1).Select(item => new AvailablePackageRuntimeItem(item)));
                _catalogErrors.Remove(family.Source);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                token.ThrowIfCancellationRequested();
                _catalogErrors[family.Source] = "Unable to check the latest LTS release. Check for updates to retry.";
                AppSessionLog.WriteError($"Could not check {family.Title} runtime releases.", error);
            }
        }
        Available.ReplaceWith(latest);
        if (_catalogErrors.Count == 0) _lastManualCheck = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(HasAvailable));
        RefreshLastCheckedText();
        RefreshFamilies();
    }

    private void RefreshLastCheckedText()
    {
        var automatic = _updateStatus?.LastCheckedAtUtc;
        var manualIsLatest = _lastManualCheck is { } manual && (automatic is null || manual >= automatic);
        var checkedAt = manualIsLatest ? _lastManualCheck : automatic;
        LastCheckedText = _updateStatus?.CheckInProgress == true ? "Checking for runtime updates…"
            : checkedAt is { } value ? $"Last checked {value.ToLocalTime():g}" : "Not checked yet";
        if (!manualIsLatest && _updateStatus?.LastErrorCode is { } code) LastCheckedText += $" · Last check: {code}";
    }

    private void RefreshFamilies()
    {
        foreach (var family in Families)
        {
            var installed = Installed.Where(item => item.Runtime.Identity.SourceId == family.Source)
                .OrderByDescending(item => item.Runtime.Identity.VendorVersion, Comparer<string>.Create(PackageRuntimeFamilyViewModel.CompareVersions)).FirstOrDefault();
            var prepared = Prepared.Where(item => item.Update.Runtimes.Any(runtime => runtime.CurrentRuntime.SourceId == family.Source))
                .OrderByDescending(item => item.CanApply || item.Update.State == RuntimeExecutionRuntimeUpdateState.ReconciliationPending)
                .ThenByDescending(item => item.Update.PreparedAtUtc).FirstOrDefault();
            family.Apply(installed, Available.FirstOrDefault(item => item.Entry.Artifact.Identity.SourceId == family.Source),
                prepared, _inventoryLoaded, _catalogErrors.GetValueOrDefault(family.Source));
        }
    }

    [RelayCommand] private Task PrimaryActionAsync(PackageRuntimeFamilyViewModel? family) => family?.Action switch
    {
        PackageRuntimeFamilyAction.Install => RunFamilyOperationAsync(family, "Preparing installation…", () => InstallAsync(family.Available)),
        PackageRuntimeFamilyAction.Update => RunFamilyOperationAsync(family, "Downloading update…", () => UpdateRuntimeAsync(family.Installed)),
        PackageRuntimeFamilyAction.Repair => RunFamilyOperationAsync(family, "Repairing…", () => RepairAsync(family.Installed)),
        _ => Task.CompletedTask,
    };
    [RelayCommand] private Task VerifyFamilyAsync(PackageRuntimeFamilyViewModel? family)
        => RunFamilyOperationAsync(family, "Checking integrity…", () => VerifyAsync(family?.Installed));
    [RelayCommand] private Task RepairFamilyAsync(PackageRuntimeFamilyViewModel? family)
        => RunFamilyOperationAsync(family, "Repairing…", () => RepairAsync(family?.Installed));
    [RelayCommand] private Task RemoveFamilyAsync(PackageRuntimeFamilyViewModel? family)
        => RunFamilyOperationAsync(family, "Removing…", () => RemoveAsync(family?.Installed));
    [RelayCommand] private Task ApplyFamilyAsync(PackageRuntimeFamilyViewModel? family)
        => RunFamilyOperationAsync(family, "Applying update…", () => ApplyWhenStoppedAsync(family?.Prepared));
    [RelayCommand] private Task RestartFamilyAsync(PackageRuntimeFamilyViewModel? family)
        => RunFamilyOperationAsync(family, "Preparing restart…", () => RestartApplyAsync(family?.Prepared));

    [RelayCommand]
    private Task InstallAsync(AvailablePackageRuntimeItem? item) => RunAsync("Preparing runtime installation…", async token =>
    {
        if (item is null) return;
        var identity = item.Entry.Artifact.Identity;
        if (!int.TryParse(identity.VendorVersion.Split('.')[0], out var major) || major < 1 || major == int.MaxValue)
            throw new InvalidOperationException("This runtime release has an invalid version. Refresh the catalog.");
        var preview = await _client.PreviewExecutionRuntimeInstallAsync(new(
            new(identity.RuntimeId, $">={identity.VendorVersion} <{major + 1}.0.0", identity.ModuleAbi), RetainUntilUninstall: true), token);
        token.ThrowIfCancellationRequested();
        if (!preview.Success || preview.Plan is null || preview.Resolution is null || !preview.RetainUntilUninstall)
            throw new InvalidOperationException(RuntimeManagementUi.Error(preview.Message, preview.ErrorCode));
        if (!await _confirm(RuntimeConfirmationViewModel.Install(preview), token)) { StatusText = "Runtime installation cancelled."; return; }
        token.ThrowIfCancellationRequested();
        QueueRuntimeOperation(identity.SourceId, $"Install {RuntimeManagementUi.Resolution(preview.Resolution)}", async (client, context) =>
        {
            var fresh = await client.PreviewExecutionRuntimeInstallAsync(new(preview.Resolution.Requirement, true), context.CancellationToken);
            if (!fresh.Success || fresh.Plan is null || fresh.Resolution?.Artifact.Identity != preview.Resolution.Artifact.Identity)
                throw PackageInstallationApproval.Changed();
            context.ReportIndeterminate("Downloading and verifying runtime…");
            var result = await client.ApplyExecutionRuntimeInstallAsync(RuntimeManagementUi.Confirm(fresh.Plan), context.CancellationToken);
            RequireSuccess(result);
            context.ReportProgress(100, result.Message);
        });
    });

    [RelayCommand]
    private Task VerifyAsync(InstalledPackageRuntimeItem? item) => RunAsync("Checking runtime integrity…", async token =>
    {
        if (item is null) return;
        var result = await _client.VerifyExecutionRuntimeAsync(item.Runtime.Identity, token);
        token.ThrowIfCancellationRequested();
        StatusText = result.Message;
        await RefreshInventoryAndStatusAsync(token);
        RequireSuccess(result);
    });

    [RelayCommand]
    private Task RepairAsync(InstalledPackageRuntimeItem? item) => RunAsync("Checking repair impact…", async token =>
    {
        if (item is null) return;
        var impact = await _client.GetExecutionRuntimeImpactAsync(item.Runtime.Identity, token);
        token.ThrowIfCancellationRequested();
        if (!await _confirm(new("Repair package runtime", "Restore this exact runtime and reload its affected packages?", "Repair & Reload",
                impact.ImpactedPackageIds, $"{item.Title}\nImpact: {impact.Fingerprint}"), token)) return;
        token.ThrowIfCancellationRequested();
        StatusText = "Repairing runtime and reloading affected packages…";
        var result = await _client.RepairExecutionRuntimeAsync(item.Runtime.Identity, impact, token);
        token.ThrowIfCancellationRequested();
        StatusText = result.Message;
        await RefreshInventoryAndStatusAsync(token);
        RequireSuccess(result);
    });

    [RelayCommand]
    private Task RemoveAsync(InstalledPackageRuntimeItem? item) => RunAsync("Checking whether this runtime can be removed…", async token =>
    {
        if (item is null || !item.CanRemove) return;
        var plan = await _client.PreviewExecutionRuntimeUninstallAsync(new(item.Runtime.Identity), token);
        token.ThrowIfCancellationRequested();
        var rowIndex = Installed.IndexOf(item);
        if (rowIndex >= 0) Installed[rowIndex] = new(item.Runtime, plan);
        if (!plan.Success || !plan.CanUninstall || plan.Plan is null)
            throw new InvalidOperationException(RuntimeManagementUi.Error(plan.Message, plan.ErrorCode));
        if (!await _confirm(new("Remove package runtime", "Remove this unused runtime and its manual retention?", "Remove Runtime",
                [item.Title], $"{plan.Message}\nReferences: {plan.Usage.PackageReferences.Count}\nActive leases: {plan.Usage.ActiveLeaseCount}\nPending transactions: {plan.Usage.PendingTransactionCount}\nImpact: {plan.Usage.Fingerprint}"), token)) return;
        token.ThrowIfCancellationRequested();
        var result = await _client.ApplyExecutionRuntimeUninstallAsync(RuntimeManagementUi.Confirm(plan.Plan), token);
        token.ThrowIfCancellationRequested();
        if (!result.Success) throw new InvalidOperationException(RuntimeManagementUi.Error(result.Message, result.ErrorCode));
        StatusText = result.RemovalPending ? "Removal scheduled. Sunder is finishing safe cleanup." : result.Message;
        await RefreshInventoryAndStatusAsync(token);
    });

    [RelayCommand]
    private Task CheckUpdatesAsync() => RunAsync("Checking for runtime updates…", async token =>
    {
        await RefreshInventoryAndStatusAsync(token);
        await LoadCatalogAsync(token);
        StatusText = Families.Any(family => family.Action == PackageRuntimeFamilyAction.Update)
            ? "LTS updates are available." : _catalogErrors.Count > 0 ? "Some runtime releases could not be checked." : "Runtime check completed.";
    });

    [RelayCommand] private Task UpdateRuntimeAsync(InstalledPackageRuntimeItem? item) => item is null ? Task.CompletedTask : PrepareUpdateAsync(item.Runtime.Identity);

    private Task PrepareUpdateAsync(RuntimeExecutionRuntimeIdentity? runtime) => RunAsync("Preparing runtime update preview…", async token =>
    {
        // Check results are advisory. Always show the exact fresh plan that is actually confirmed.
        var plan = await _client.PreviewExecutionRuntimeUpdateAsync(new(Runtime: runtime), token);
        token.ThrowIfCancellationRequested();
        RequireUpdatePlan(plan);
        if (plan.Runtimes.Count == 0) { StatusText = plan.Message; return; }
        if (!await _confirm(RuntimeConfirmationViewModel.Update(plan, restart: false), token)) return;
        token.ThrowIfCancellationRequested();
        StatusText = "Downloading runtime update. Your packages keep running…";
        var result = await _client.PrepareExecutionRuntimeUpdateAsync(RuntimeManagementUi.Confirm(plan.Plan!), token);
        token.ThrowIfCancellationRequested();
        await ShowUpdateResultAsync(result, token);
        if (result.Success && result.Update is { } prepared && !result.StoreCommitted)
            await ApplyPreparedAsync(prepared.UpdateId, restart: false, token);
        await RefreshInventoryAndStatusAsync(token);
    });

    [RelayCommand]
    private Task ApplyWhenStoppedAsync(PreparedPackageRuntimeItem? item) => RunAsync("Checking whether the downloaded update can apply…", async token =>
    {
        if (item is null || !item.CanApply) return;
        await ApplyPreparedAsync(item.Update.UpdateId, restart: false, token);
        await RefreshInventoryAndStatusAsync(token);
    });

    [RelayCommand]
    private Task RestartApplyAsync(PreparedPackageRuntimeItem? item) => RunAsync("Checking fresh restart impact…", async token =>
    {
        if (item is null || !item.CanApply) return;
        await ApplyPreparedAsync(item.Update.UpdateId, restart: true, token);
        await RefreshInventoryAndStatusAsync(token);
    });

    private async Task ApplyPreparedAsync(string updateId, bool restart, CancellationToken token)
    {
        var plan = await _client.PreviewExecutionRuntimeUpdateAsync(new(PreparedUpdateId: updateId), token);
        token.ThrowIfCancellationRequested();
        RequireUpdatePlan(plan);
        if (plan.PreparedUpdateId != updateId) throw new InvalidOperationException("The prepared update changed. Refresh and review it again.");
        if (restart && !await _confirm(RuntimeConfirmationViewModel.Update(plan, restart: true), token)) { StatusText = "Restart cancelled. The downloaded update is still waiting."; return; }
        token.ThrowIfCancellationRequested();
        var result = await _client.ApplyExecutionRuntimeUpdateAsync(new(RuntimeManagementUi.Confirm(plan.Plan!), plan.Usage.Fingerprint,
            restart ? RuntimeExecutionRuntimeApplyMode.RestartAffectedPackages : RuntimeExecutionRuntimeApplyMode.WhenStopped), token);
        token.ThrowIfCancellationRequested();
        await ShowUpdateResultAsync(result, token);
    }

    private async Task ShowUpdateResultAsync(RuntimeExecutionRuntimeUpdateResult result, CancellationToken token)
    {
        StatusText = result.State switch
        {
            RuntimeExecutionRuntimeUpdateState.Prepared or RuntimeExecutionRuntimeUpdateState.Deferred => "Downloaded, waiting for packages to stop. Use Restart & Apply when you are ready.",
            RuntimeExecutionRuntimeUpdateState.Applied when result.StoreCommitted && result.RuntimeSessionApplied => "Runtime update applied. Package versions are unchanged.",
            RuntimeExecutionRuntimeUpdateState.ReconciliationPending when result.StoreCommitted => "Runtime bindings saved; waiting for the running session to catch up.",
            RuntimeExecutionRuntimeUpdateState.ReconciliationPending => "Runtime update is awaiting reconciliation; no committed binding change was confirmed.",
            RuntimeExecutionRuntimeUpdateState.Applied when !result.StoreCommitted => "Runtime update reported completion, but no committed binding change was confirmed. Refresh to check its status.",
            _ when result.StoreCommitted => "Runtime bindings saved. " + result.Message,
            _ => result.Message,
        };
        if (!result.Success || result.State is RuntimeExecutionRuntimeUpdateState.Stale or RuntimeExecutionRuntimeUpdateState.Blocked
            or RuntimeExecutionRuntimeUpdateState.Error)
            ErrorText = RuntimeManagementUi.Error(result.Message, result.ErrorCode);
        if (result.StoreCommitted && result.RuntimeSessionApplied && result.CommittedStamp is { } stamp && _waitForPresentation is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
            try
            {
                var presentation = await _waitForPresentation(stamp, waiting.Token);
                token.ThrowIfCancellationRequested();
                if (presentation.Outcome != PackagePresentationOutcome.Applied)
                    StatusText = "Runtime bindings saved; the App presentation is pending. " + presentation.Message;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                StatusText = "Runtime bindings saved; the App presentation has not caught up yet. Refresh to check its status.";
            }
        }
    }

    private static void RequireUpdatePlan(RuntimeExecutionRuntimeUpdatePlan plan)
    {
        if (!plan.Success || (plan.Runtimes.Count > 0 && plan.Plan is null))
            throw new InvalidOperationException(RuntimeManagementUi.Error(plan.Message, plan.ErrorCode));
    }
    private static void RequireSuccess(RuntimeExecutionRuntimeOperationResult result)
    {
        if (!result.Success) throw new InvalidOperationException(RuntimeManagementUi.Error(result.Message, result.ErrorCode));
    }

    [RelayCommand] private void Cancel() => _operation?.Cancel();

    private void QueueRuntimeOperation(ConfiguredOfficialSourceId source, string title, Func<IRuntimeExecutionRuntimeManagementClient, BackgroundProcessContext, Task> action)
    {
        var createClient = _createOperationClient;
        _queue.Enqueue(new(title, PackageOperationService.PackageStoreGroupKey, BackgroundProcessIndicator.Settings,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup, true, async context =>
            {
                using var client = createClient();
                await action(client, context).ConfigureAwait(false);
            }, Metadata: new Dictionary<string, string> { [RuntimeSourceMetadataKey] = source.ToString() }));
        RefreshQueuedOperations();
        StatusText = "Runtime installation queued. It continues if you close Settings; cancel it in Background Processes.";
    }

    private async Task<bool> RunAsync(string status, Func<CancellationToken, Task> action)
    {
        if (!CanAct || _disposed) return false;
        var page = _page!;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(page.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operationCompletion = completion;
        _operation = operation;
        IsBusy = true;
        _refreshCancellation?.Cancel();
        ErrorText = string.Empty;
        StatusText = status;
        var entered = false;
        try
        {
            await _requests.WaitAsync(operation.Token);
            entered = true;
            await action(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (ReferenceEquals(_page, page)) StatusText = "Cancelled. Refresh to check any completed runtime changes.";
            return false;
        }
        catch (Exception error)
        {
            if (ReferenceEquals(_page, page)) ErrorText = error is Sunder.Runtime.Client.RuntimeClientException runtimeError
                ? RuntimeManagementUi.Error(runtimeError.Message, runtimeError.ErrorCode) : error.Message;
            AppSessionLog.WriteError("Package runtime management failed.", error);
            return false;
        }
        finally
        {
            if (entered) _requests.Release();
            if (ReferenceEquals(_operation, operation)) _operation = null;
            IsBusy = false;
            if (ReferenceEquals(_operationCompletion, completion)) _operationCompletion = null;
            completion.TrySetResult();
            RefreshFamilies();
        }
    }

    private void NotifyActions()
    {
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(CanChangePreference));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Deactivate();
        _tasks.Dispose();
        _client.Dispose();
    }
}

public sealed record InstalledPackageRuntimeItem(InstalledExecutionRuntimeSummary Runtime, RuntimeExecutionRuntimeUninstallPlan? Removal, string? UsageError = null)
{
    public string Title => RuntimeManagementUi.Identity(Runtime.Identity);
    public string Status => $"{Runtime.Status} · {Runtime.Support.State} · {RegistryPackageChangePreflight.FormatBytes(Runtime.Size)}";
    public string Health => Runtime.Health.Message ?? $"Health: {Runtime.Health.Status}";
    public string Retention => Runtime.RetainedUntilUninstall ? "Kept until you remove it" : "Managed by package usage";
    public string UsedBy => Removal is null ? $"Usage details unavailable: {UsageError}"
        : Removal.Usage.PackageReferences.Count == 0 && Removal.Usage.RunningPackageIds.Count == 0 ? "Ready for your packages."
        : "Used by: " + string.Join(", ", Removal.Usage.PackageReferences.Select(item => item.PackageId)
            .Concat(Removal.Usage.RunningPackageIds).Distinct(StringComparer.OrdinalIgnoreCase));
    public bool CanRemove => Runtime.PackageReferenceCount == 0 && Removal is { Success: true, CanUninstall: true };
    public string RemovalReason => CanRemove ? "Unused runtime can be removed." : Removal?.Message ?? "Refresh to check complete usage before removing.";
    public string Details => $"Source: {RuntimeManagementUi.Source(Runtime.Identity)}\nPlatform: {Runtime.Identity.Rid}\nSize: {RegistryPackageChangePreflight.FormatBytes(Runtime.Size)}\nInstalled: {Runtime.InstalledAtUtc.ToLocalTime():g}";
}

public sealed record AvailablePackageRuntimeItem(RuntimeExecutionRuntimeCatalogEntry Entry)
{
    public string Title => RuntimeManagementUi.Identity(Entry.Artifact.Identity);
    public string Summary => $"{RuntimeManagementUi.Source(Entry.Artifact.Identity)} · {Entry.Artifact.Support.State} · {RegistryPackageChangePreflight.FormatBytes(Entry.Artifact.Distribution.DownloadSize)}";
    public string InstallText => "Install Compatible Runtime";
    public string Details => $"Source: {RuntimeManagementUi.Source(Entry.Artifact.Identity)}\nPlatform: {Entry.Artifact.Identity.Rid}\nDownload: {RegistryPackageChangePreflight.FormatBytes(Entry.Artifact.Distribution.DownloadSize)}";
}

public sealed record PreparedPackageRuntimeItem(RuntimeExecutionRuntimePreparedUpdate Update)
{
    public string Title => string.Join(", ", Update.Runtimes.Select(item => RuntimeManagementUi.Resolution(item.Resolution)));
    public string Status => Update.State switch
    {
        RuntimeExecutionRuntimeUpdateState.Prepared or RuntimeExecutionRuntimeUpdateState.Deferred => "Downloaded, waiting for packages to stop",
        RuntimeExecutionRuntimeUpdateState.Applied => "Applied",
        RuntimeExecutionRuntimeUpdateState.ReconciliationPending => "Awaiting session reconciliation",
        RuntimeExecutionRuntimeUpdateState.Stale => "Preview changed. Check for updates again.",
        RuntimeExecutionRuntimeUpdateState.Blocked => "Update blocked. Check the current runtime status.",
        RuntimeExecutionRuntimeUpdateState.Error => "Update did not complete. Check for updates to retry.",
        _ => "No runtime changes",
    };
    public bool CanApply => Update.State is RuntimeExecutionRuntimeUpdateState.Prepared or RuntimeExecutionRuntimeUpdateState.Deferred;
    public string Details => "Affected package bindings: " + string.Join(", ", Update.BindingChanges.Select(item => $"{item.PackageId} {item.PackageVersion}").Distinct());
}
