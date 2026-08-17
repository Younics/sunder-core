using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

public sealed record SettingsRuntimeResourcePresetOption(
    RuntimeResourcePolicyPreset Preset,
    string Label,
    string Description);

public sealed class SettingsRuntimeResourcesViewModel : ViewModelBase
{
    private static readonly IReadOnlyList<SettingsRuntimeResourcePresetOption> Presets =
    [
        new(
            RuntimeResourcePolicyPreset.Standard,
            "Standard",
            "Balanced limits for normal package RPC workloads."),
        new(
            RuntimeResourcePolicyPreset.Large,
            "Large",
            "Higher limits for data-heavy local workflows."),
        new(
            RuntimeResourcePolicyPreset.Custom,
            "Custom",
            "Set each Runtime resource limit explicitly."),
    ];

    private readonly SettingsRuntimeResourcesCoordinator _coordinator;
    private RuntimeResourcePolicy _preservedCustomPolicy = RuntimeResourcePolicies.Standard;
    private RuntimeResourcePolicyEffectiveStatus _currentStatus =
        RuntimeResourcePolicyEffectiveStatus.Unavailable(
            "Current effective policy has not been requested.");
    private SettingsRuntimeResourcePresetOption _selectedPresetOption = Presets[0];
    private string _customRpcValueMiBText = "8";
    private string _customValueDepthText = "64";
    private string _customValueNodesText = "524288";
    private string _customObjectMembersText = "4096";
    private string _customInlineDecodedKiBText = "512";
    private string _customContentReferenceMiBText = "64";
    private string _validationMessage = string.Empty;
    private string _desiredPresetText = "Standard";
    private string _desiredEffectiveText = string.Empty;
    private string _desiredProfileHash = string.Empty;
    private string _currentEffectiveText = "Unavailable";
    private string _currentProfileHash = "Not reported";
    private string _currentStatusText = string.Empty;
    private bool _showPendingNotice = true;
    private string _pendingDetailText = string.Empty;
    private bool _updating;

    internal SettingsRuntimeResourcesViewModel(SettingsRuntimeResourcesCoordinator coordinator)
    {
        _coordinator = coordinator;
        ResetToDefaultsCommand = new RelayCommand(ResetToDefaults);
        UpdatePresentation();
    }

    public IReadOnlyList<SettingsRuntimeResourcePresetOption> PresetOptions => Presets;

    public IRelayCommand ResetToDefaultsCommand { get; }

    public SettingsRuntimeResourcePresetOption SelectedPresetOption
    {
        get => _selectedPresetOption;
        set
        {
            if (value is null || !SetProperty(ref _selectedPresetOption, value))
            {
                return;
            }
            OnPropertyChanged(nameof(IsCustom));
            UpdatePresentation();
        }
    }

    public bool IsCustom => SelectedPresetOption.Preset == RuntimeResourcePolicyPreset.Custom;

    public string CustomRpcValueMiBText
    {
        get => _customRpcValueMiBText;
        set => SetCustomText(ref _customRpcValueMiBText, value);
    }

    public string CustomValueDepthText
    {
        get => _customValueDepthText;
        set => SetCustomText(ref _customValueDepthText, value);
    }

    public string CustomValueNodesText
    {
        get => _customValueNodesText;
        set => SetCustomText(ref _customValueNodesText, value);
    }

    public string CustomObjectMembersText
    {
        get => _customObjectMembersText;
        set => SetCustomText(ref _customObjectMembersText, value);
    }

    public string CustomInlineDecodedKiBText
    {
        get => _customInlineDecodedKiBText;
        set => SetCustomText(ref _customInlineDecodedKiBText, value);
    }

    public string CustomContentReferenceMiBText
    {
        get => _customContentReferenceMiBText;
        set => SetCustomText(ref _customContentReferenceMiBText, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public bool HasValidationError => !string.IsNullOrEmpty(ValidationMessage);

    public string DesiredPresetText
    {
        get => _desiredPresetText;
        private set => SetProperty(ref _desiredPresetText, value);
    }

    public string DesiredEffectiveText
    {
        get => _desiredEffectiveText;
        private set => SetProperty(ref _desiredEffectiveText, value);
    }

    public string DesiredProfileHash
    {
        get => _desiredProfileHash;
        private set => SetProperty(ref _desiredProfileHash, value);
    }

    public string CurrentEffectiveText
    {
        get => _currentEffectiveText;
        private set => SetProperty(ref _currentEffectiveText, value);
    }

    public string CurrentProfileHash
    {
        get => _currentProfileHash;
        private set => SetProperty(ref _currentProfileHash, value);
    }

    public string CurrentStatusText
    {
        get => _currentStatusText;
        private set => SetProperty(ref _currentStatusText, value);
    }

    public bool ShowPendingNotice
    {
        get => _showPendingNotice;
        private set => SetProperty(ref _showPendingNotice, value);
    }

    public string PendingDetailText
    {
        get => _pendingDetailText;
        private set => SetProperty(ref _pendingDetailText, value);
    }

    internal void LoadSettings()
    {
        var selection = _coordinator.LoadSettings();
        _preservedCustomPolicy = selection.CustomPolicy;
        _updating = true;
        try
        {
            SelectedPresetOption = Presets.Single(option => option.Preset == selection.Preset);
            ApplyCustomPolicy(selection.CustomPolicy);
        }
        finally
        {
            _updating = false;
        }
        UpdatePresentation();
    }

    internal async Task RefreshCurrentEffectivePolicyAsync(CancellationToken cancellationToken)
    {
        try
        {
            _currentStatus = await _coordinator.GetCurrentEffectivePolicyAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _currentStatus = RuntimeResourcePolicyEffectiveStatus.Unavailable(
                $"Current effective policy could not be read: {exception.Message}");
        }
        UpdatePresentation();
    }

    internal async Task<SettingsRuntimeResourceSaveResult> SaveSettingsAsync(
        CancellationToken cancellationToken)
    {
        if (!TryCreateSelection(out var selection, out var validationMessage))
        {
            ValidationMessage = validationMessage;
            return new SettingsRuntimeResourceSaveResult(false, validationMessage);
        }

        var result = await _coordinator.SaveSettingsAsync(selection!, cancellationToken);
        if (result.Success)
        {
            UpdatePresentation();
        }
        return result;
    }

    private void ResetToDefaults()
    {
        _preservedCustomPolicy = RuntimeResourcePolicies.Standard;
        _updating = true;
        try
        {
            SelectedPresetOption = Presets[0];
            ApplyCustomPolicy(RuntimeResourcePolicies.Standard);
        }
        finally
        {
            _updating = false;
        }
        UpdatePresentation();
    }

    private void ApplyCustomPolicy(RuntimeResourcePolicy policy)
    {
        CustomRpcValueMiBText = ToUnitText(policy.MaximumRpcValueBytes, RuntimeResourcePolicies.Mebibyte);
        CustomValueDepthText = policy.MaximumValueDepth.ToString(CultureInfo.InvariantCulture);
        CustomValueNodesText = policy.MaximumValueNodes.ToString(CultureInfo.InvariantCulture);
        CustomObjectMembersText = policy.MaximumObjectMembers.ToString(CultureInfo.InvariantCulture);
        CustomInlineDecodedKiBText = ToUnitText(
            policy.MaximumInlineDecodedBytes,
            RuntimeResourcePolicies.Kibibyte);
        CustomContentReferenceMiBText = ToUnitText(
            policy.MaximumContentReferenceBytes,
            RuntimeResourcePolicies.Mebibyte);
    }

    private void SetCustomText(ref string field, string? value)
    {
        if (SetProperty(ref field, value ?? string.Empty) && !_updating)
        {
            UpdatePresentation();
        }
    }

    private void UpdatePresentation()
    {
        if (_updating)
        {
            return;
        }

        DesiredPresetText = SelectedPresetOption.Label;
        if (!TryCreateSelection(out var selection, out var validationMessage))
        {
            ValidationMessage = IsCustom ? validationMessage : string.Empty;
            DesiredEffectiveText = IsCustom ? "Custom values are invalid." : FormatPolicy(
                SelectedPresetOption.Preset == RuntimeResourcePolicyPreset.Large
                    ? RuntimeResourcePolicies.Large
                    : RuntimeResourcePolicies.Standard);
            DesiredProfileHash = IsCustom ? "Unavailable" : RuntimeResourcePolicyContract.GetEffectiveProfileHash(
                SelectedPresetOption.Preset == RuntimeResourcePolicyPreset.Large
                    ? RuntimeResourcePolicies.Large
                    : RuntimeResourcePolicies.Standard);
            ShowPendingNotice = false;
            PendingDetailText = string.Empty;
        }
        else
        {
            ValidationMessage = string.Empty;
            var effectivePolicy = selection!.EffectivePolicy;
            DesiredEffectiveText = FormatPolicy(effectivePolicy);
            DesiredProfileHash = selection.EffectiveProfileHash;
            ShowPendingNotice = !_currentStatus.IsAvailable
                                || !string.Equals(
                                    _currentStatus.EffectiveProfileHash,
                                    selection.EffectiveProfileHash,
                                    StringComparison.Ordinal);
            PendingDetailText = _currentStatus.IsAvailable
                ? "The saved policy takes effect on the next Runtime worker start. Applying does not restart Runtime."
                : "The current Runtime does not report this profile yet. Applying saves only; the policy is loaded on the next Runtime worker start.";
        }

        if (_currentStatus.EffectivePolicy is { } currentPolicy)
        {
            CurrentEffectiveText = FormatPolicy(currentPolicy);
            CurrentProfileHash = _currentStatus.EffectiveProfileHash!;
            CurrentStatusText = "Reported by the running Runtime.";
        }
        else
        {
            CurrentEffectiveText = "Unavailable";
            CurrentProfileHash = "Not reported";
            CurrentStatusText = _currentStatus.UnavailableReason ?? "Current effective policy is unavailable.";
        }
    }

    private bool TryCreateSelection(
        out RuntimeResourcePolicySelection? selection,
        out string validationMessage)
    {
        selection = null;
        if (!TryReadInRange(
                CustomRpcValueMiBText,
                "RPC value limit",
                1,
                32,
                out var rpcValueMiB,
                out validationMessage)
            || !TryReadInRange(
                CustomValueDepthText,
                "Value depth",
                16,
                64,
                out var valueDepth,
                out validationMessage)
            || !TryReadInRange(
                CustomValueNodesText,
                "Value nodes",
                16_384,
                4_194_304,
                out var valueNodes,
                out validationMessage)
            || !TryReadInRange(
                CustomObjectMembersText,
                "Object members",
                256,
                65_536,
                out var objectMembers,
                out validationMessage)
            || !TryReadInRange(
                CustomInlineDecodedKiBText,
                "Inline decoded data",
                16,
                4_096,
                out var inlineDecodedKiB,
                out validationMessage)
            || !TryReadInRange(
                CustomContentReferenceMiBText,
                "Content reference",
                1,
                256,
                out var contentReferenceMiB,
                out validationMessage))
        {
            if (!IsCustom)
            {
                selection = new RuntimeResourcePolicySelection(
                    SelectedPresetOption.Preset,
                    _preservedCustomPolicy);
                validationMessage = string.Empty;
                return true;
            }
            return false;
        }

        var custom = new RuntimeResourcePolicy(
            checked(rpcValueMiB * RuntimeResourcePolicies.Mebibyte),
            valueDepth,
            valueNodes,
            objectMembers,
            checked(inlineDecodedKiB * RuntimeResourcePolicies.Kibibyte),
            checked(contentReferenceMiB * RuntimeResourcePolicies.Mebibyte));
        _preservedCustomPolicy = custom;
        selection = new RuntimeResourcePolicySelection(SelectedPresetOption.Preset, custom);
        validationMessage = string.Empty;
        return true;
    }

    private static bool TryReadInRange(
        string text,
        string label,
        int minimum,
        int maximum,
        out int value,
        out string validationMessage)
    {
        if (!int.TryParse(
                text,
                NumberStyles.Integer | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out value)
            || value < minimum
            || value > maximum)
        {
            validationMessage = $"{label} must be a whole number from {minimum:N0} to {maximum:N0}.";
            return false;
        }
        validationMessage = string.Empty;
        return true;
    }

    private static string FormatPolicy(RuntimeResourcePolicy policy)
        => $"{FormatBytes(policy.MaximumRpcValueBytes)} RPC value; depth {policy.MaximumValueDepth:N0}; "
           + $"{policy.MaximumValueNodes:N0} nodes; {policy.MaximumObjectMembers:N0} object members; "
           + $"{FormatBytes(policy.MaximumInlineDecodedBytes)} inline decoded; "
           + $"{FormatBytes(policy.MaximumContentReferenceBytes)} content reference";

    private static string FormatBytes(int bytes)
        => bytes % RuntimeResourcePolicies.Mebibyte == 0
            ? $"{bytes / RuntimeResourcePolicies.Mebibyte:N0} MiB"
            : $"{bytes / RuntimeResourcePolicies.Kibibyte:N0} KiB";

    private static string ToUnitText(int bytes, int unit)
        => (bytes / unit).ToString(CultureInfo.InvariantCulture);
}

internal sealed class SettingsRuntimeResourcesCoordinator(
    AppRuntimeResourcePolicySettingsService settingsService,
    IRuntimeResourcePolicyStatusSource statusSource)
{
    public RuntimeResourcePolicySelection LoadSettings()
        => settingsService.Load();

    public ValueTask<RuntimeResourcePolicyEffectiveStatus> GetCurrentEffectivePolicyAsync(
        CancellationToken cancellationToken)
        => statusSource.GetCurrentEffectivePolicyAsync(cancellationToken);

    public async Task<SettingsRuntimeResourceSaveResult> SaveSettingsAsync(
        RuntimeResourcePolicySelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            await settingsService.SaveAsync(selection, cancellationToken);
            return new SettingsRuntimeResourceSaveResult(
                true,
                "Runtime resource policy saved. Pending until Runtime restarts.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SettingsRuntimeResourceSaveResult(false, exception.Message);
        }
    }
}

internal sealed record SettingsRuntimeResourceSaveResult(bool Success, string StatusText);
