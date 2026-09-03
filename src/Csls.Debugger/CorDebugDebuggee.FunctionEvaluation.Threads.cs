using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Isolates and restores managed threads around debugger-owned function evaluation.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private unsafe void SuspendOtherThreads(
        int evaluationThreadId,
        Dictionary<int, int> states)
    {
        nint enumerator = 0;
        try
        {
            nint* enumeratorAddress = &enumerator;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugControllerAbi(_debugProcess).EnumerateThreads(
                    (nint)enumeratorAddress),
                "ICorDebugController.EnumerateThreads");
            enumerator = RequirePointer(
                Volatile.Read(ref *enumeratorAddress),
                "ICorDebugController.EnumerateThreads");
            var threads = new ICorDebugThreadEnumAbi(enumerator);
            while (true)
            {
                nint thread = 0;
                uint fetched = 0;
                nint* threadAddress = &thread;
                uint* fetchedAddress = &fetched;
                CorDebugHResult.ThrowIfFailed(
                    threads.Next(1, (nint)threadAddress, (nint)fetchedAddress),
                    "ICorDebugThreadEnum.Next");
                thread = Volatile.Read(ref *threadAddress);
                if (Volatile.Read(ref *fetchedAddress) == 0)
                {
                    return;
                }

                try
                {
                    int threadId = checked((int)GetManagedThreadId(thread));
                    if (threadId == evaluationThreadId)
                    {
                        continue;
                    }

                    int state = GetThreadDebugState(thread);
                    states.Add(threadId, state);
                    if (state != 1)
                    {
                        CorDebugHResult.ThrowIfFailed(
                            new ICorDebugThreadAbi(thread).SetDebugState(1),
                            "ICorDebugThread.SetDebugState");
                    }
                }
                finally
                {
                    if (thread != 0)
                    {
                        _ = ComAbi.Release(thread);
                    }
                }
            }
        }
        finally
        {
            if (enumerator != 0)
            {
                _ = ComAbi.Release(enumerator);
            }
        }
    }

    private void RestoreThreadStates(ManagedFunctionEvaluation evaluation) =>
        RestoreThreadStates(evaluation.ThreadId, evaluation.ThreadStates);

    private unsafe void RestoreThreadStates(
        int evaluationThreadId,
        IReadOnlyDictionary<int, int> states)
    {
        nint enumerator = 0;
        try
        {
            nint* enumeratorAddress = &enumerator;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugControllerAbi(_debugProcess).EnumerateThreads(
                    (nint)enumeratorAddress),
                "ICorDebugController.EnumerateThreads");
            enumerator = RequirePointer(
                Volatile.Read(ref *enumeratorAddress),
                "ICorDebugController.EnumerateThreads");
            var threads = new ICorDebugThreadEnumAbi(enumerator);
            while (true)
            {
                nint thread = 0;
                uint fetched = 0;
                nint* threadAddress = &thread;
                uint* fetchedAddress = &fetched;
                CorDebugHResult.ThrowIfFailed(
                    threads.Next(1, (nint)threadAddress, (nint)fetchedAddress),
                    "ICorDebugThreadEnum.Next");
                thread = Volatile.Read(ref *threadAddress);
                if (Volatile.Read(ref *fetchedAddress) == 0)
                {
                    return;
                }

                try
                {
                    int threadId = checked((int)GetManagedThreadId(thread));
                    int state = threadId == evaluationThreadId
                        ? 0
                        : states.GetValueOrDefault(threadId, 0);
                    CorDebugHResult.ThrowIfFailed(
                        new ICorDebugThreadAbi(thread).SetDebugState(state),
                        "ICorDebugThread.SetDebugState");
                }
                finally
                {
                    if (thread != 0)
                    {
                        _ = ComAbi.Release(thread);
                    }
                }
            }
        }
        finally
        {
            if (enumerator != 0)
            {
                _ = ComAbi.Release(enumerator);
            }
        }
    }

    private static unsafe int GetThreadDebugState(nint thread)
    {
        int state = 0;
        int* stateAddress = &state;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugThreadAbi(thread).GetDebugState((nint)stateAddress),
            "ICorDebugThread.GetDebugState");
        return Volatile.Read(ref *stateAddress);
    }

    private static unsafe uint GetManagedThreadId(nint thread)
    {
        uint threadId = 0;
        uint* threadIdAddress = &threadId;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugThreadAbi(thread).GetID((nint)threadIdAddress),
            "ICorDebugThread.GetID");
        return Volatile.Read(ref *threadIdAddress);
    }
}
