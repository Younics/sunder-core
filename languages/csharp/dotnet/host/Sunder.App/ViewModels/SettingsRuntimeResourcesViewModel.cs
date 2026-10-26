using System.Globalization;
using System.Runtime.CompilerServices;
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
    private RuntimeResourcePolicySelection _savedSelection = RuntimeResourcePolicies.DefaultSelection;
    private string _customRpcValueMiBText = "32";
    private string _customValueDepthText = "64";
    private string _customValueNodesText = "4194304";
    private string _customObjectMembersText = "65536";
    private string _customInlineDecodedKiBText = "4096";
    private string _customContentReferenceMiBText = "256";
    private string _validationMessage = string.Empty;
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
            OnPropertyChanged(nameof(AreLimitsReadOnly));
            NotifyLimitFields();
            UpdatePresentation();
        }
    }

    public bool IsCustom => SelectedPresetOption.Preset == RuntimeResourcePolicyPreset.Custom;
    public bool AreLimitsReadOnly => !IsCustom;
    private RuntimeResourcePolicy PresetPolicy => SelectedPresetOption.Preset == RuntimeResourcePolicyPreset.Large
        ? RuntimeResourcePolicies.Large : RuntimeResourcePolicies.Standard;

    public string RpcValueRange => Range(RuntimeResourcePolicies.CustomMinimums.MaximumRpcValueBytes,
        RuntimeResourcePolicies.ProtocolCeilings.MaximumRpcValueBytes, RuntimeResourcePolicies.Mebibyte, "MiB per encoded RPC value");
    public string ValueDepthRange => Range(RuntimeResourcePolicies.CustomMinimums.MaximumValueDepth,
        RuntimeResourcePolicies.ProtocolCeilings.MaximumValueDepth, 1, "JSON levels (not RPC call depth)");
    public string ValueNodesRange => Range(RuntimeResourcePolicies.CustomMinimums.MaximumValueNodes,
        RuntimeResourcePolicies.ProtocolCeilings.MaximumValueNodes, 1, "nodes");
    public string ObjectMembersRange => Range(RuntimeResourcePolicies.CustomMinimums.MaximumObjectMembers,
        RuntimeResourcePolicies.ProtocolCeilings.MaximumObjectMembers, 1, "members per object");
    public string InlineDecodedRange => Range(RuntimeResourcePolicies.CustomMinimums.MaximumInlineDecodedBytes,
        RuntimeResourcePolicies.ProtocolCeilings.MaximumInlineDecodedBytes, RuntimeResourcePolicies.Kibibyte, "KiB decoded binary");
    public string ContentReferenceRange => Range(RuntimeResourcePolicies.CustomMinimums.MaximumContentReferenceBytes,
        RuntimeResourcePolicies.ProtocolCeilings.MaximumContentReferenceBytes, RuntimeResourcePolicies.Mebibyte, "MiB per referenced content item");

    public string CustomRpcValueMiBText
    {
        get => IsCustom ? _customRpcValueMiBText : ToUnitText(PresetPolicy.MaximumRpcValueBytes, RuntimeResourcePolicies.Mebibyte);
        set => SetCustomText(ref _customRpcValueMiBText, value);
    }

    public string CustomValueDepthText
    {
        get => IsCustom ? _customValueDepthText : ToUnitText(PresetPolicy.MaximumValueDepth, 1);
        set => SetCustomText(ref _customValueDepthText, value);
    }

    public string CustomValueNodesText
    {
        get => IsCustom ? _customValueNodesText : ToUnitText(PresetPolicy.MaximumValueNodes, 1);
        set => SetCustomText(ref _customValueNodesText, value);
    }

    public string CustomObjectMembersText
    {
        get => IsCustom ? _customObjectMembersText : ToUnitText(PresetPolicy.MaximumObjectMembers, 1);
        set => SetCustomText(ref _customObjectMembersText, value);
    }

    public string CustomInlineDecodedKiBText
    {
        get => IsCustom ? _customInlineDecodedKiBText : ToUnitText(PresetPolicy.MaximumInlineDecodedBytes, RuntimeResourcePolicies.Kibibyte);
        set => SetCustomText(ref _customInlineDecodedKiBText, value);
    }

    public string CustomContentReferenceMiBText
    {
        get => IsCustom ? _customContentReferenceMiBText : ToUnitText(PresetPolicy.MaximumContentReferenceBytes, RuntimeResourcePolicies.Mebibyte);
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
        _savedSelection = selection;
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
            _savedSelection = selection!;
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

    private void SetCustomText(ref string field, string? value, [CallerMemberName] string? propertyName = null)
    {
        if (!IsCustom && !_updating) return;
        if (SetProperty(ref field, value ?? string.Empty, propertyName) && !_updating)
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

        if (!TryCreateSelection(out var selection, out var validationMessage))
        {
            ValidationMessage = validationMessage;
        }
        else
        {
            ValidationMessage = string.Empty;
        }
        ShowPendingNotice = !_currentStatus.IsAvailable
                            || !string.Equals(_currentStatus.EffectiveProfileHash,
                                _savedSelection.EffectiveProfileHash, StringComparison.Ordinal);
        PendingDetailText = selection is null || selection.EffectiveProfileHash != _savedSelection.EffectiveProfileHash
            ? "Apply or Save to keep your changes. Saved changes take effect when Runtime next starts; saving does not restart it."
            : _currentStatus.IsAvailable
                ? "Saved changes take effect when Runtime next starts. Saving does not restart Runtime."
                : "The running Runtime profile is unavailable. Saved changes take effect when Runtime next starts.";
    }

    private bool TryCreateSelection(
        out RuntimeResourcePolicySelection? selection,
        out string validationMessage)
    {
        selection = null;
        if (!IsCustom)
        {
            selection = new RuntimeResourcePolicySelection(SelectedPresetOption.Preset, _preservedCustomPolicy);
            validationMessage = string.Empty;
            return true;
        }
        if (!TryReadInRange(
                CustomRpcValueMiBText,
                "RPC value limit",
                RuntimeResourcePolicies.CustomMinimums.MaximumRpcValueBytes / RuntimeResourcePolicies.Mebibyte,
                RuntimeResourcePolicies.MaximumRpcValueBytes / RuntimeResourcePolicies.Mebibyte,
                out var rpcValueMiB,
                out validationMessage)
            || !TryReadInRange(
                CustomValueDepthText,
                "Value depth",
                RuntimeResourcePolicies.CustomMinimums.MaximumValueDepth,
                RuntimeResourcePolicies.MaximumValueDepth,
                out var valueDepth,
                out validationMessage)
            || !TryReadInRange(
                CustomValueNodesText,
                "Value nodes",
                RuntimeResourcePolicies.CustomMinimums.MaximumValueNodes,
                RuntimeResourcePolicies.MaximumValueNodes,
                out var valueNodes,
                out validationMessage)
            || !TryReadInRange(
                CustomObjectMembersText,
                "Object members",
                RuntimeResourcePolicies.CustomMinimums.MaximumObjectMembers,
                RuntimeResourcePolicies.MaximumObjectMembers,
                out var objectMembers,
                out validationMessage)
            || !TryReadInRange(
                CustomInlineDecodedKiBText,
                "Inline decoded data",
                RuntimeResourcePolicies.CustomMinimums.MaximumInlineDecodedBytes / RuntimeResourcePolicies.Kibibyte,
                RuntimeResourcePolicies.MaximumInlineDecodedBytes / RuntimeResourcePolicies.Kibibyte,
                out var inlineDecodedKiB,
                out validationMessage)
            || !TryReadInRange(
                CustomContentReferenceMiBText,
                "Content reference",
                RuntimeResourcePolicies.CustomMinimums.MaximumContentReferenceBytes / RuntimeResourcePolicies.Mebibyte,
                RuntimeResourcePolicies.MaximumContentReferenceBytes / RuntimeResourcePolicies.Mebibyte,
                out var contentReferenceMiB,
                out validationMessage))
        {
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

    private void NotifyLimitFields()
    {
        OnPropertyChanged(nameof(CustomRpcValueMiBText));
        OnPropertyChanged(nameof(CustomValueDepthText));
        OnPropertyChanged(nameof(CustomValueNodesText));
        OnPropertyChanged(nameof(CustomObjectMembersText));
        OnPropertyChanged(nameof(CustomInlineDecodedKiBText));
        OnPropertyChanged(nameof(CustomContentReferenceMiBText));
    }

    private static string Range(int minimum, int maximum, int unit, string suffix)
        => $"{minimum / unit:N0}–{maximum / unit:N0} {suffix}";

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
