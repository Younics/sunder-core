using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed partial class StacksWindowViewModel
{
    private int BeginSelectedRegistryStackDetailsLoad(RegistryStackSearchItemViewModel? value)
    {
        CancelSelectedRegistryStackDetailsLoad();
        CancelRegistryStackDetailsSpinnerDelay();
        var selectionVersion = ++_registrySelectionVersion;
        ApplySelectedRegistryStackSummary(value);

        RegistryStackDetailsError = string.Empty;
        RegistryStackDetailsLoaded = false;
        ShowRegistryStackDetailsSpinner = false;
        if (value is null)
        {
            IsRegistryStackDetailsLoading = false;
        }
        else
        {
            _registryStackDetailsCancellation = CancellationTokenSource.CreateLinkedTokenSource(_tasks.Token);
            IsRegistryStackDetailsLoading = true;
            QueueRegistryStackDetailsSpinner(value, selectionVersion);
        }

        NotifyRegistryStackDetailsStateChanged();
        return selectionVersion;
    }

    private void CompleteSelectedRegistryStackDetailsLoad(string? errorMessage = null)
    {
        CancelRegistryStackDetailsSpinnerDelay();
        ShowRegistryStackDetailsSpinner = false;
        RegistryStackDetailsError = errorMessage ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            SelectedRegistryStackSummary = "Stack details could not be loaded.";
        }

        RegistryStackDetailsLoaded = string.IsNullOrWhiteSpace(errorMessage);
        IsRegistryStackDetailsLoading = false;
        NotifyRegistryStackDetailsStateChanged();
    }

    private void QueueRegistryStackDetailsSpinner(RegistryStackSearchItemViewModel stack, int selectionVersion)
    {
        var spinnerCancellation = new CancellationTokenSource();
        _registryStackDetailsSpinnerCancellation = spinnerCancellation;
        _tasks.Observe(
            ShowRegistryStackDetailsSpinnerAfterDelayAsync(stack, selectionVersion, spinnerCancellation),
            "delaying Registry Stack details spinner");
    }

    private async Task ShowRegistryStackDetailsSpinnerAfterDelayAsync(
        RegistryStackSearchItemViewModel stack,
        int selectionVersion,
        CancellationTokenSource spinnerCancellation)
    {
        try
        {
            await Task.Delay(_registryDetailSpinnerDelay, spinnerCancellation.Token);
            if (!_disposed
                && selectionVersion == _registrySelectionVersion
                && ReferenceEquals(SelectedRegistryStack, stack)
                && IsRegistryStackDetailsLoading
                && !RegistryStackDetailsLoaded)
            {
                ShowRegistryStackDetailsSpinner = true;
                OnPropertyChanged(nameof(ShowRegistryStackDetailsLoading));
            }
        }
        catch (OperationCanceledException) when (spinnerCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_registryStackDetailsSpinnerCancellation, spinnerCancellation))
            {
                _registryStackDetailsSpinnerCancellation = null;
            }

            spinnerCancellation.Dispose();
        }
    }

    private void CancelRegistryStackDetailsSpinnerDelay()
    {
        var spinnerCancellation = _registryStackDetailsSpinnerCancellation;
        if (spinnerCancellation is null)
        {
            return;
        }

        _registryStackDetailsSpinnerCancellation = null;
        spinnerCancellation.Cancel();
    }

    private void CancelSelectedRegistryStackDetailsLoad()
    {
        var selectionCancellation = _registryStackDetailsCancellation;
        if (selectionCancellation is null)
        {
            return;
        }

        _registryStackDetailsCancellation = null;
        selectionCancellation.Cancel();
    }

    private void ApplySelectedRegistryStackSummary(RegistryStackSearchItemViewModel? value)
    {
        RegistrySelectedPackages.Clear();
        DisposeRegistrySelectedDetails();
        RegistrySelectedRequiredInputs.Clear();
        ApplyRegistryStackProfile(null);
        ApplyRegistryAttributions(null, []);

        if (value is null)
        {
            SelectedRegistryStackTitle = "No Marketplace Stack selected";
            SelectedRegistryStackSubtitle = "Search Marketplace Stacks to inspect shared setup presets.";
            SelectedRegistryStackSummary = string.Empty;
            SelectedRegistryStackContentText = string.Empty;
            SelectedRegistryStackUpdatedText = string.Empty;
            ClearSelectedRegistryStackStats();
            NotifyRegistrySelectedDetailsChanged();
            return;
        }

        SelectedRegistryStackTitle = value.Name;
        SelectedRegistryStackSubtitle = value.StackId;
        SelectedRegistryStackSummary = string.Empty;
        SelectedRegistryStackContentText = string.Empty;
        SelectedRegistryStackUpdatedText = string.Empty;
        ClearSelectedRegistryStackStats();
        NotifyRegistrySelectedDetailsChanged();
    }

    private async Task LoadSelectedRegistryStackDetailsAsync(
        string stackId,
        int selectionVersion,
        CancellationTokenSource selectionCancellation)
    {
        var cancellationToken = selectionCancellation.Token;
        if (!TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            CompleteSelectedRegistryStackDetailsLoad("Enter a valid HTTP Registry URL before using Registry Stacks.");
            return;
        }

        try
        {
            using var registryClient = _registryClientFactory(registryUrl);
            var details = await registryClient.GetStackAsync(stackId, cancellationToken);
            if (selectionVersion != _registrySelectionVersion)
            {
                return;
            }

            if (details is null)
            {
                CompleteSelectedRegistryStackDetailsLoad($"Registry Stack '{stackId}' was not found.");
                StatusText = RegistryStackDetailsError;
                return;
            }

            var packageInfo = await LoadRegistryDetailPackageInfoAsync(
                registryClient,
                details.Fragments,
                cancellationToken);
            if (selectionVersion != _registrySelectionVersion)
            {
                return;
            }

            ApplySelectedRegistryStackDetails(details, packageInfo);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (selectionVersion == _registrySelectionVersion)
            {
                CompleteSelectedRegistryStackDetailsLoad(ex.Message);
                StatusText = ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_registryStackDetailsCancellation, selectionCancellation))
            {
                _registryStackDetailsCancellation = null;
            }

            selectionCancellation.Dispose();
        }
    }

    private void ApplySelectedRegistryStackDetails(
        RegistryStackDetails details,
        IReadOnlyDictionary<string, StackPackageInfo> packageInfo)
    {
        SelectedRegistryStackTitle = details.Name;
        SelectedRegistryStackSubtitle = details.StackId;
        SelectedRegistryStackSummary = string.IsNullOrWhiteSpace(details.Summary) ? string.Empty : details.Summary;
        SelectedRegistryStackContentText = $"{details.Packages.Count} package{(details.Packages.Count == 1 ? string.Empty : "s")} · {details.Fragments.Count} fragment{(details.Fragments.Count == 1 ? string.Empty : "s")}";
        SelectedRegistryStackUpdatedText = $"Updated {details.UpdatedAtUtc.LocalDateTime:g}";
        ApplySelectedRegistryStackStats(details.Stats);
        ApplyRegistryStackProfile(details.Profile);
        ApplyRegistryAttributions(details.Creator, details.Maintainers ?? []);

        RegistrySelectedPackages.Clear();
        foreach (var package in details.Packages)
        {
            RegistrySelectedPackages.Add(new RegistryStackPackageRequirementViewModel(package));
        }

        PopulateRegistrySelectedDetails(details.Fragments, packageInfo);

        RegistrySelectedRequiredInputs.Clear();
        foreach (var input in details.RequiredInputs)
        {
            RegistrySelectedRequiredInputs.Add(input.Label);
        }

        CompleteSelectedRegistryStackDetailsLoad();
        NotifyRegistrySelectedDetailsChanged();
    }

    private void ApplyRegistryStackProfile(RegistryStackProfile? profile)
    {
        var shortDescription = _registryProfile.Apply(profile);
        if (!string.IsNullOrWhiteSpace(shortDescription))
        {
            SelectedRegistryStackSummary = shortDescription;
        }

        NotifyRegistryProfileChanged();
    }

    private void ApplyRegistryAttributions(
        RegistryUserAttribution? creator,
        IReadOnlyList<RegistryUserAttribution> maintainers)
    {
        var creators = creator is null
            ? Array.Empty<RegistryUserAttributionViewModel>()
            : new[] { new RegistryUserAttributionViewModel(creator) };
        var maintainerAttributions = maintainers
            .Where(maintainer => creator is null || !maintainer.IsOwner)
            .Select(maintainer => new RegistryUserAttributionViewModel(maintainer))
            .ToArray();

        RegistryCreators.ReplaceWith(creators);
        RegistryMaintainers.ReplaceWith(maintainerAttributions);
        RegistryAttributions.ReplaceWith(creators.Concat(maintainerAttributions));
        OnPropertyChanged(nameof(HasRegistryAttributions));
        OnPropertyChanged(nameof(HasRegistryCreators));
        OnPropertyChanged(nameof(HasRegistryMaintainers));
    }

    private void ClearSelectedRegistryStackStats()
    {
        HasSelectedRegistryStackStats = false;
        SelectedRegistryStackStatsText = string.Empty;
        SelectedRegistryStackIsStarred = false;
        SelectedRegistryStackStarActionText = "Star";
    }

    private void ApplySelectedRegistryStackStats(RegistryStackStats? stats)
    {
        if (stats is null)
        {
            return;
        }

        HasSelectedRegistryStackStats = true;
        SelectedRegistryStackStatsText = FormatStackStats(stats);
        SelectedRegistryStackIsStarred = stats.IsStarred;
        SelectedRegistryStackStarActionText = stats.IsStarred ? "Unstar" : "Star";
        NotifyRegistrySelectedDetailsChanged();
        NotifyCommandStateChanged();
    }

}
