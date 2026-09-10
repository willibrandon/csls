namespace Csls.TestProcessHost;

/// <summary>
/// Provides stable asynchronous source positions for managed stepping tests.
/// </summary>
internal static class DebuggerAsyncStepFixture
{
    /// <summary>
    /// Suspends at an incomplete await and resumes at a later source statement.
    /// </summary>
    /// <param name="initialValue">The runtime-provided value to increment.</param>
    /// <returns>Zero when asynchronous stepping preserves the computed result.</returns>
    internal static async Task<int> RunAsync(int initialValue)
    {
        int answer = initialValue;
        await Task.Delay(250).ConfigureAwait(false);
        answer++;
        return answer == 42 ? 0 : 1;
    }

    /// <summary>
    /// Runs competing state-machine instances whose continuations complete out of order.
    /// </summary>
    /// <returns>Zero when both asynchronous computations complete correctly.</returns>
    internal static async Task<int> RunConcurrentAsync()
    {
        Task<int> selected = DelayAndIncrementAsync(41, 350);
        Task<int> competing = DelayAndIncrementAsync(10, 25);
        int[] results = await Task.WhenAll(selected, competing).ConfigureAwait(false);
        return results is [42, 11] ? 0 : 1;
    }

    private static async Task<int> DelayAndIncrementAsync(int value, int delayMilliseconds)
    {
        await Task.Delay(delayMilliseconds).ConfigureAwait(false);
        value++;
        return value;
    }
}
