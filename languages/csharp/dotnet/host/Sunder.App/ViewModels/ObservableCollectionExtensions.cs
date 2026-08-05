using System.Collections.ObjectModel;

namespace Sunder.App.ViewModels;

internal static class ObservableCollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    public static void SyncWith<T>(
        this ObservableCollection<T> target,
        IEnumerable<T> items,
        IEqualityComparer<T>? comparer = null,
        Action<T>? removeItem = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        var nextItems = items as IReadOnlyList<T> ?? items.ToArray();

        for (var index = target.Count - 1; index >= 0; index--)
        {
            var item = target[index];
            if (Contains(nextItems, item, comparer))
            {
                continue;
            }

            target.RemoveAt(index);
            removeItem?.Invoke(item);
        }

        for (var targetIndex = 0; targetIndex < nextItems.Count; targetIndex++)
        {
            var nextItem = nextItems[targetIndex];
            if (targetIndex < target.Count && comparer.Equals(target[targetIndex], nextItem))
            {
                continue;
            }

            var existingIndex = IndexOf(target, nextItem, targetIndex + 1, comparer);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, targetIndex);
                continue;
            }

            target.Insert(targetIndex, nextItem);
        }

        while (target.Count > nextItems.Count)
        {
            var item = target[^1];
            target.RemoveAt(target.Count - 1);
            removeItem?.Invoke(item);
        }
    }

    private static bool Contains<T>(IReadOnlyList<T> items, T item, IEqualityComparer<T> comparer)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (comparer.Equals(items[index], item))
            {
                return true;
            }
        }

        return false;
    }

    private static int IndexOf<T>(ObservableCollection<T> items, T item, int startIndex, IEqualityComparer<T> comparer)
    {
        for (var index = startIndex; index < items.Count; index++)
        {
            if (comparer.Equals(items[index], item))
            {
                return index;
            }
        }

        return -1;
    }
}
