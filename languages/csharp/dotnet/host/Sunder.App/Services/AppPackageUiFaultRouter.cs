using System.Reflection;
using System.Runtime.InteropServices;

namespace Sunder.App.Services;

internal static class AppPackageUiFaultPolicy
{
    public static bool IsRecoverable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (IsFatal(exception))
        {
            return false;
        }

        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }
            if (IsFatal(current))
            {
                return false;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current is ReflectionTypeLoadException reflectionTypeLoad)
            {
                foreach (var loaderException in reflectionTypeLoad.LoaderExceptions)
                {
                    if (loaderException is not null)
                    {
                        pending.Push(loaderException);
                    }
                }
                if (current.InnerException is not null)
                {
                    pending.Push(current.InnerException);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        return true;
    }

    private static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or SEHException;
}

internal sealed class AppPackageUiFaultRouter
{
    public bool TryRouteKnownViewFailure(
        string? packageId,
        string? viewId,
        Exception exception,
        Func<string, string, Exception, bool> acceptQuarantine)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(acceptQuarantine);
        return AppPackageUiFaultPolicy.IsRecoverable(exception)
            && !string.IsNullOrWhiteSpace(packageId)
            && !string.IsNullOrWhiteSpace(viewId)
            && acceptQuarantine(packageId, viewId, exception);
    }

    public bool TryRoutePackageFailure(
        Exception exception,
        Func<Exception, string?> resolveExclusivePackageId,
        Func<string, Exception, bool> acceptQuarantine)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(resolveExclusivePackageId);
        ArgumentNullException.ThrowIfNull(acceptQuarantine);
        if (!AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            return false;
        }

        var packageId = resolveExclusivePackageId(exception);
        return !string.IsNullOrWhiteSpace(packageId)
            && acceptQuarantine(packageId, exception);
    }
}
