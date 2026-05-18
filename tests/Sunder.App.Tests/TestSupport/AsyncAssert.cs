using Xunit;

namespace Sunder.App.Tests.TestSupport;

internal static class AsyncAssert
{
    public static async Task WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        var delay = pollInterval ?? TimeSpan.FromMilliseconds(10);

        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.True(condition());
                return;
            }

            await Task.Delay(delay);
        }
    }
}
