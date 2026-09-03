using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Enumerates managed threads at a runtime-consistent stop.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Enumerates managed threads while the target is stopped.
    /// </summary>
    /// <returns>A bounded snapshot of current managed threads.</returns>
    internal unsafe IReadOnlyList<DebugThreadInfo> GetThreads()
    {
        const int maximumThreadCount = 4096;
        nint enumerator = 0;
        nint* enumeratorAddress = &enumerator;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugControllerAbi(_debugProcess).EnumerateThreads((nint)enumeratorAddress),
            "ICorDebugController.EnumerateThreads");
        enumerator = Volatile.Read(ref *enumeratorAddress);
        if (enumerator == 0)
        {
            throw new InvalidOperationException(
                "ICorDebugController.EnumerateThreads returned no enumerator.");
        }

        try
        {
            var result = new List<DebugThreadInfo>();
            var api = new ICorDebugThreadEnumAbi(enumerator);
            while (result.Count < maximumThreadCount)
            {
                nint thread = 0;
                uint fetched = 0;
                nint* threadAddress = &thread;
                uint* fetchedAddress = &fetched;
                int nextResult = api.Next(1, (nint)threadAddress, (nint)fetchedAddress);
                CorDebugHResult.ThrowIfFailed(nextResult, "ICorDebugThreadEnum.Next");
                thread = Volatile.Read(ref *threadAddress);
                fetched = Volatile.Read(ref *fetchedAddress);
                if (fetched == 0)
                {
                    break;
                }

                try
                {
                    uint threadId = 0;
                    uint* threadIdAddress = &threadId;
                    CorDebugHResult.ThrowIfFailed(
                        new ICorDebugThreadAbi(thread).GetID((nint)threadIdAddress),
                        "ICorDebugThread.GetID");
                    int id = checked((int)threadId);
                    result.Add(new DebugThreadInfo(id, $"Thread {id}"));
                }
                finally
                {
                    if (thread != 0)
                    {
                        _ = ComAbi.Release(thread);
                    }
                }
            }

            if (result.Count == maximumThreadCount)
            {
                throw new InvalidOperationException(
                    $"The target exceeds the managed-thread limit of {maximumThreadCount}.");
            }

            return result;
        }
        finally
        {
            _ = ComAbi.Release(enumerator);
        }
    }
}
