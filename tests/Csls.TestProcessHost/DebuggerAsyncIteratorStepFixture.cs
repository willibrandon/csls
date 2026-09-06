using System.IO.Pipes;

namespace Csls.TestProcessHost;

/// <summary>
/// Coordinates an asynchronous iterator and its await-foreach consumer through a real pipe.
/// </summary>
internal static class DebuggerAsyncIteratorStepFixture
{
    /// <summary>
    /// Reports a suspended consumer before allowing the test to release its iterator.
    /// </summary>
    /// <param name="pipeName">The test-owned duplex pipe used to coordinate suspension.</param>
    /// <returns>Zero when both yielded values reach the consumer.</returns>
    internal static async Task<int> RunAsync(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync().ConfigureAwait(false);
        return await Task.Factory.StartNew(() => ObserveSuspensionAsync(pipe), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap().ConfigureAwait(false);
    }

    private static async Task<int> ObserveSuspensionAsync(NamedPipeClientStream pipe)
    {
        Task<int> pending = ConsumeAsync(pipe, coordination: null, identity: 1);
        if (pending.IsCompleted)
        {
            return 2;
        }

        await pipe.WriteAsync(new byte[] { 1 }).ConfigureAwait(false);
        return await pending.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs competing consumers whose completion order is controlled by the test's two pipes.
    /// </summary>
    internal static async Task<int> RunConcurrentAsync(string pipeName)
    {
        using var selected = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var competing = new NamedPipeClientStream(".", pipeName + "-c", PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(selected.ConnectAsync(), competing.ConnectAsync()).ConfigureAwait(false);
        return await Task.Factory.StartNew(() => ObserveConcurrentSuspensionAsync(selected, competing),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap().ConfigureAwait(false);
    }

    private static async Task<int> ObserveConcurrentSuspensionAsync(NamedPipeClientStream selected, NamedPipeClientStream competing)
    {
        Task<int> pending = ConsumeAsync(selected, selected, identity: 1);
        Task<int> other = ConsumeAsync(competing, coordination: null, identity: 2);
        if (pending.IsCompleted || other.IsCompleted)
        {
            return 2;
        }

        await selected.WriteAsync(new byte[] { 1 }).ConfigureAwait(false);
        int otherResult = await other.ConfigureAwait(false);
        await selected.WriteAsync(new byte[] { 3 }).ConfigureAwait(false);
        return otherResult + await pending.ConfigureAwait(false);
    }

    private static async Task<int> ConsumeAsync(NamedPipeClientStream pipe, NamedPipeClientStream? coordination, int identity)
    {
        int total = 0;
        await foreach (int value in ReadAndEnumerateAsync(pipe, coordination).ConfigureAwait(false))
        {
            total += value;
        }

        return total == 83 && identity is 1 or 2 ? 0 : 1;
    }

    private static async IAsyncEnumerable<int> ReadAndEnumerateAsync(NamedPipeClientStream pipe, NamedPipeClientStream? coordination)
    {
        int value = 40;
        byte[] buffer = new byte[1];
        await pipe.ReadExactlyAsync(buffer.AsMemory()).ConfigureAwait(false);
        value += buffer[0];
        yield return CollectAndReturn(value, coordination);
        value++;
        yield return checked(value);
    }

    private static int CollectAndReturn(int value, NamedPipeClientStream? coordination)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        if (coordination is not null)
        {
            coordination.WriteByte(2);
            if (coordination.ReadByte() != 1)
            {
                throw new IOException("The selected iterator was not released by its consumer-order handshake.");
            }
        }

        return value;
    }
}
