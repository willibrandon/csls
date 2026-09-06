using System.IO.Pipes;

namespace Csls.TestProcessHost;

/// <summary>
/// Coordinates suspended Task and ValueTask callees and their awaiting callers.
/// </summary>
internal static class DebuggerAsyncStepOutFixture
{
    /// <summary>
    /// Runs an asynchronous caller after proving its callee has suspended on a pipe read.
    /// </summary>
    internal static async Task<int> RunAsync(string pipeName, bool valueTask, bool scheduled = false)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync().ConfigureAwait(false);
        if (scheduled)
        {
            var scheduler = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, maxConcurrencyLevel: 1);
            try
            {
                return await Task.Factory.StartNew(() => ObserveAsync(pipe, valueTask), CancellationToken.None,
                    TaskCreationOptions.None, scheduler.ExclusiveScheduler).Unwrap().ConfigureAwait(false);
            }
            finally
            {
                scheduler.Complete();
                await scheduler.Completion.ConfigureAwait(false);
            }
        }

        return await Task.Factory.StartNew(() => ObserveAsync(pipe, valueTask), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap().ConfigureAwait(false);
    }

    private static async Task<int> ObserveAsync(NamedPipeClientStream pipe, bool valueTask)
    {
        Task<int> pending = valueTask ? ConsumeValueTaskAsync(pipe, null, 1) : ConsumeTaskAsync(pipe, null, 1);
        if (pending.IsCompleted)
        {
            return 2;
        }

        await pipe.WriteAsync(new byte[] { 1 }).ConfigureAwait(false);
        return await pending.ConfigureAwait(false);
    }

    private static async Task<int> ConsumeTaskAsync(NamedPipeClientStream pipe, NamedPipeClientStream? coordination, int identity)
    {
        int answer = await ReadTaskAsync(pipe, coordination).ConfigureAwait(true);
        return ValidateResult(ref answer, identity); // task caller
    }

    private static async Task<int> ConsumeValueTaskAsync(NamedPipeClientStream pipe, NamedPipeClientStream? coordination, int identity)
    {
        int answer = await ReadValueTaskAsync(pipe, coordination).ConfigureAwait(true);
        return ValidateResult(ref answer, identity); // value task caller
    }

    private static int ValidateResult(ref int answer, int identity) => answer == 42 && identity is 1 or 2 ? 0 : 3;

    private static async Task<int> ReadTaskAsync(NamedPipeClientStream pipe, NamedPipeClientStream? coordination)
    {
        int answer = 40;
        byte[] buffer = new byte[1];
        await pipe.ReadExactlyAsync(buffer).ConfigureAwait(false); // task suspension
        answer += buffer[0]; // task resumption
        try
        {
            return CollectAndReturn(answer + 1, coordination);
        }
        finally
        {
            ValidateReadResult(answer); // task finally
        }
    }

    private static async ValueTask<int> ReadValueTaskAsync(NamedPipeClientStream pipe, NamedPipeClientStream? coordination)
    {
        int answer = 40;
        byte[] buffer = new byte[1];
        await pipe.ReadExactlyAsync(buffer).ConfigureAwait(false); // value task suspension
        answer += buffer[0]; // value task resumption
        try
        {
            return CollectAndReturn(answer + 1, coordination);
        }
        finally
        {
            ValidateReadResult(answer); // value task finally
        }
    }

    private static void ValidateReadResult(int answer)
    {
        if (answer != 41)
        {
            throw new IOException("The asynchronous callee received an invalid pipe handshake.");
        }
    }

    private static int CollectAndReturn(int value, NamedPipeClientStream? coordination)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        if (coordination is not null)
        {
            coordination.WriteByte(2);
            if (coordination.ReadByte() != 1)
            {
                throw new IOException("The selected asynchronous callee was not released by its caller-order handshake.");
            }
        }

        return value;
    }

    /// <summary>
    /// Runs callers whose completion order is controlled by two test-owned duplex pipes.
    /// </summary>
    internal static async Task<int> RunConcurrentAsync(string pipeName, bool valueTask)
    {
        using var selected = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var competing = new NamedPipeClientStream(".", pipeName + "-c", PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(selected.ConnectAsync(), competing.ConnectAsync()).ConfigureAwait(false);
        return await Task.Factory.StartNew(() => ObserveConcurrentAsync(selected, competing, valueTask),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap().ConfigureAwait(false);
    }

    private static async Task<int> ObserveConcurrentAsync(NamedPipeClientStream selected, NamedPipeClientStream competing, bool valueTask)
    {
        Task<int> pending = valueTask ? ConsumeValueTaskAsync(selected, selected, 1) : ConsumeTaskAsync(selected, selected, 1);
        Task<int> other = valueTask ? ConsumeValueTaskAsync(competing, null, 2) : ConsumeTaskAsync(competing, null, 2);
        if (pending.IsCompleted || other.IsCompleted)
        {
            return 2;
        }

        await selected.WriteAsync(new byte[] { 1 }).ConfigureAwait(false);
        int otherResult = await other.ConfigureAwait(false);
        await selected.WriteAsync(new byte[] { 3 }).ConfigureAwait(false);
        return otherResult + await pending.ConfigureAwait(false);
    }
}
