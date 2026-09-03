using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Serializes managed debugger callback work and applies session decisions.
/// </summary>
internal sealed partial class CorDebugManagedCallback
{
    private static int QueueContinue(nint self, nint controller, bool createsProcess) =>
        QueueCallback(
            self,
            controller,
            thread: 0,
            subject: 0,
            auxiliary: 0,
            createsProcess,
            exitsProcess: false,
            continueAfterCallback: true,
            callbackOperation: null);

    private static int QueueCallback(
        nint self,
        nint controller,
        nint thread,
        nint subject,
        nint auxiliary,
        bool createsProcess,
        bool exitsProcess,
        bool continueAfterCallback,
        CorDebugCallbackOperation? callbackOperation)
    {
        if (controller == 0)
        {
            return NullPointerHResult;
        }

        CorDebugManagedCallback target = GetTarget(self);
        _ = ComAbi.AddRef(controller);
        if (thread != 0)
        {
            _ = ComAbi.AddRef(thread);
        }

        if (subject != 0)
        {
            _ = ComAbi.AddRef(subject);
        }

        if (auxiliary != 0)
        {
            _ = ComAbi.AddRef(auxiliary);
        }

        nint ownedController = controller;
        nint ownedThread = thread;
        nint ownedSubject = subject;
        nint ownedAuxiliary = auxiliary;
        Task queuedOperation = target._actor.InvokeAsync(
            async actorCancellationToken =>
            {
                nint currentController = Interlocked.Exchange(ref ownedController, 0);
                nint currentThread = Interlocked.Exchange(ref ownedThread, 0);
                nint currentSubject = Interlocked.Exchange(ref ownedSubject, 0);
                nint currentAuxiliary = Interlocked.Exchange(ref ownedAuxiliary, 0);
                try
                {
                    bool detaching = Volatile.Read(ref target._detaching) != 0;
                    bool shouldContinue = continueAfterCallback && !detaching;
                    if (!detaching)
                    {
                        try
                        {
                            if (callbackOperation is not null)
                            {
                                shouldContinue = await callbackOperation(
                                    target,
                                    currentThread,
                                    currentSubject,
                                    currentAuxiliary,
                                    actorCancellationToken).ConfigureAwait(false);
                            }
                        }
                        catch (Exception exception) when (IsRecoverableCallbackFailure(exception))
                        {
                            if (createsProcess)
                            {
                                _ = target._createProcessCompletion.TrySetException(exception);
                            }

                            shouldContinue = continueAfterCallback;
                        }
                    }

                    if (shouldContinue && Volatile.Read(ref target._detaching) == 0)
                    {
                        int result = new ICorDebugControllerAbi(currentController)
                            .Continue(fIsOutOfBand: 0);
                        if (createsProcess)
                        {
                            _ = target._createProcessCompletion.TrySetResult(result);
                        }

                        CorDebugHResult.ThrowIfFailed(result, "ICorDebugController.Continue");
                    }
                }
                finally
                {
                    if (currentAuxiliary != 0)
                    {
                        _ = ComAbi.Release(currentAuxiliary);
                    }

                    if (currentSubject != 0)
                    {
                        _ = ComAbi.Release(currentSubject);
                    }

                    if (currentThread != 0)
                    {
                        _ = ComAbi.Release(currentThread);
                    }

                    _ = ComAbi.Release(currentController);
                }

                if (exitsProcess)
                {
                    _ = target._exitProcessCompletion.TrySetResult();
                }
            },
            CancellationToken.None);
        _ = target.ObserveOperationAsync(
            queuedOperation,
            () =>
            {
                nint currentAuxiliary = Interlocked.Exchange(ref ownedAuxiliary, 0);
                if (currentAuxiliary != 0)
                {
                    _ = ComAbi.Release(currentAuxiliary);
                }

                nint currentSubject = Interlocked.Exchange(ref ownedSubject, 0);
                if (currentSubject != 0)
                {
                    _ = ComAbi.Release(currentSubject);
                }

                nint currentThread = Interlocked.Exchange(ref ownedThread, 0);
                if (currentThread != 0)
                {
                    _ = ComAbi.Release(currentThread);
                }

                nint currentController = Interlocked.Exchange(ref ownedController, 0);
                if (currentController != 0)
                {
                    _ = ComAbi.Release(currentController);
                }
            },
            createsProcess,
            exitsProcess);
        return SuccessHResult;
    }

    private async Task ObserveOperationAsync(
        Task operation,
        Action releaseUnclaimed,
        bool createsProcess,
        bool exitsProcess)
    {
        try
        {
            await operation.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverableCallbackFailure(exception))
        {
            releaseUnclaimed();
            if (createsProcess)
            {
                _ = _createProcessCompletion.TrySetException(exception);
            }

            if (exitsProcess)
            {
                _ = _exitProcessCompletion.TrySetException(exception);
            }
        }
    }

    private static bool IsRecoverableCallbackFailure(Exception exception) =>
        exception is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or BadImageFormatException or
            OperationCanceledException or System.Threading.Channels.ChannelClosedException;
}
