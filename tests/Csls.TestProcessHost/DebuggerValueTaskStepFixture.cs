using System.IO.Pipes;

namespace Csls.TestProcessHost;

/// <summary>
/// Coordinates a genuine asynchronous pipe read for ValueTask and finally-block stepping.
/// </summary>
internal static class DebuggerValueTaskStepFixture
{
    /// <summary>
    /// Reports an incomplete ValueTask before the test releases its pipe read.
    /// </summary>
    /// <param name="pipeName">The test-owned duplex pipe used to coordinate suspension.</param>
    /// <returns>Zero when resumption and the finally block preserve the expected result.</returns>
    internal static async Task<int> RunAsync(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync().ConfigureAwait(false);
        return await Task.Factory.StartNew(() => ObserveSuspensionAsync(pipe), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap().ConfigureAwait(false);
    }

    private static async Task<int> ObserveSuspensionAsync(NamedPipeClientStream pipe)
    {
        ValueTask<int> pending = ReadAndIncrementAsync(pipe);
        if (pending.IsCompleted)
        {
            return 2;
        }

        await pipe.WriteAsync(new byte[] { 1 }).ConfigureAwait(false);
        return await pending.ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadAndIncrementAsync(NamedPipeClientStream pipe)
    {
        int answer = 40;
        byte[] buffer = new byte[1];
        int received;
        try
        {
            int scopedIncrement = 1;
            received = await pipe.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            answer += scopedIncrement * buffer[0];
        }
        finally
        {
            answer++;
        }

        return received == 1 && answer == 42 ? 0 : 1;
    }
}
