using System.Diagnostics;
using Avalonia.Controls;
using Sunder.App.Features.Shell.Layout;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Features.Shell.Panels;

internal sealed class ShellPackageViewPreloader(
    IReadOnlyDictionary<string, ShellPackageView> viewsById,
    ShellState shellState,
    PackageViewHostService packageViewHostService,
    Func<IReadOnlyList<ShellPlacementSlot>> getSlots,
    IUiDispatcher uiDispatcher,
    bool logStageTiming = false)
{
    public async Task PreloadAsync(
        Func<Func<CancellationToken, Task>, CancellationToken, Task> runWhenInputIdleAsync,
        CancellationToken cancellationToken)
    {
        var generationId = packageViewHostService.CurrentGenerationId;
        var priorityQueue = await uiDispatcher
            .InvokeAsync(BuildPriorityQueue, cancellationToken)
            .ConfigureAwait(false);
        foreach (var viewId in priorityQueue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (packageViewHostService.CurrentGenerationId != generationId)
            {
                return;
            }

            await runWhenInputIdleAsync(
                    async operationCancellation =>
                    {
                        operationCancellation.ThrowIfCancellationRequested();
                        if (packageViewHostService.CurrentGenerationId != generationId)
                        {
                            return;
                        }

                        var stopwatch = Stopwatch.StartNew();
                        if (logStageTiming)
                        {
                            AppSessionLog.WriteInfo(
                                $"Package view preload started for '{viewId}' in App generation '{generationId}'.");
                        }
                        var control = await packageViewHostService
                            .PreloadViewAsync(
                                viewId,
                                generationId,
                                operationCancellation,
                                retainedControl => !operationCancellation.IsCancellationRequested
                                    && RetainIfCurrent(viewId, retainedControl, generationId))
                            .ConfigureAwait(false);
                        if (control is null)
                        {
                            if (logStageTiming)
                            {
                                AppSessionLog.WriteInfo(
                                    $"Package view preload skipped for '{viewId}' after {stopwatch.ElapsedMilliseconds} ms.");
                            }
                            return;
                        }

                        if (logStageTiming)
                        {
                            AppSessionLog.WriteInfo(
                                $"Package view preload construction/warmup/retention for '{viewId}': {stopwatch.ElapsedMilliseconds} ms.");
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public void RetainHotbarViews()
    {
        var generationId = packageViewHostService.CurrentGenerationId;
        foreach (var viewId in BuildHotbarQueue())
        {
            if (packageViewHostService.CurrentGenerationId != generationId
                || !viewsById.ContainsKey(viewId))
            {
                return;
            }

            var control = packageViewHostService.GetOrCreateView(viewId);
            if (control is not null)
            {
                RetainIfCurrent(viewId, control, generationId);
            }
        }
    }

    private IReadOnlyList<string> BuildPriorityQueue()
    {
        var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = BuildHotbarQueue(queued);
        var slots = getSlots();

        foreach (var slot in slots)
        {
            foreach (var view in viewsById.Values
                         .Where(view => view.Placement == slot.Placement)
                         .Where(view => shellState.HiddenHotbarViewIds.Contains(view.ViewId))
                         .OrderBy(view => view.PackageDisplayName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(view => view.Title, StringComparer.OrdinalIgnoreCase))
            {
                AddViewId(view.ViewId, queued, result);
            }
        }

        return result;
    }

    private List<string> BuildHotbarQueue()
        => BuildHotbarQueue(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private List<string> BuildHotbarQueue(ISet<string> queued)
    {
        var result = new List<string>();
        var slots = getSlots();
        foreach (var slot in slots)
        {
            AddItems(slot.Bar.VisibleItems, queued, result);
        }

        foreach (var slot in slots)
        {
            AddItems(slot.Bar.OverflowItems, queued, result);
        }

        return result;
    }

    private void AddItems(
        IEnumerable<ShellItemViewModel> items,
        ISet<string> queued,
        ICollection<string> result)
    {
        foreach (var item in items)
        {
            if (viewsById.ContainsKey(item.Id))
            {
                AddViewId(item.Id, queued, result);
            }
        }
    }

    private static void AddViewId(
        string viewId,
        ISet<string> queued,
        ICollection<string> result)
    {
        if (queued.Add(viewId))
        {
            result.Add(viewId);
        }
    }

    private bool RetainIfCurrent(string viewId, Control control, Guid generationId)
    {
        if (packageViewHostService.CurrentGenerationId != generationId
            || !viewsById.TryGetValue(viewId, out var packageView))
        {
            return false;
        }

        var panel = getSlots()
            .First(slot => slot.Placement == packageView.Placement)
            .Panel;
        if (panel.GetRetainedView(viewId) is not null)
        {
            return true;
        }

        var boundary = packageViewHostService.CreateHostedViewBoundary(
            packageView.PackageId,
            viewId,
            control);
        if (boundary is not null)
        {
            panel.RetainHostedView(viewId, boundary);
            return true;
        }
        return false;
    }
}
