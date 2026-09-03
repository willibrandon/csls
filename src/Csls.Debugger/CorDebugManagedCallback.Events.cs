using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Handles managed step, module, and exception callbacks.
/// </summary>
internal sealed partial class CorDebugManagedCallback
{
    private ValueTask<bool> HandleStepCompleteAsync(
        nint thread,
        nint stepper,
        int reason,
        CancellationToken cancellationToken)
    {
        if (thread == 0 || stepper == 0)
        {
            return ValueTask.FromResult(true);
        }

        return CompleteStepAsync(thread, stepper, reason, cancellationToken);
    }

    private async ValueTask<bool> CompleteStepAsync(
        nint thread,
        nint stepper,
        int reason,
        CancellationToken cancellationToken)
    {
        bool recognized = await _stepCompleted(
            checked((int)GetThreadId(thread)),
            stepper,
            reason,
            cancellationToken).ConfigureAwait(false);
        return !recognized;
    }

    private async ValueTask<bool> HandleLoadModuleAsync(
        nint module,
        CancellationToken cancellationToken)
    {
        await _sourceBreakpoints.LoadModuleAsync(module, cancellationToken).ConfigureAwait(false);
        await _functionBreakpoints.LoadModuleAsync(
            module,
            _sourceBreakpoints.FindModule(module)?.ModuleImage,
            cancellationToken).ConfigureAwait(false);
        await _instructionBreakpoints.LoadModuleAsync(
            module,
            _sourceBreakpoints.FindModule(module)?.Id,
            cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async ValueTask<bool> HandleUnloadModuleAsync(
        nint module,
        CancellationToken cancellationToken)
    {
        await _sourceBreakpoints.UnloadModuleAsync(module, cancellationToken)
            .ConfigureAwait(false);
        await _functionBreakpoints.UnloadModuleAsync(module, cancellationToken)
            .ConfigureAwait(false);
        await _instructionBreakpoints.UnloadModuleAsync(module, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async ValueTask<bool> HandleLoadClassAsync(
        nint @class,
        CancellationToken cancellationToken)
    {
        if (@class == 0)
        {
            return true;
        }

        nint module = 0;
        try
        {
            module = GetClassModule(@class);
            if (module != 0)
            {
                await _sourceBreakpoints.RefreshInMemorySymbolsAsync(
                    module,
                    cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            if (module != 0)
            {
                _ = ComAbi.Release(module);
            }
        }
    }

    private static unsafe nint GetClassModule(nint @class)
    {
        nint module = 0;
        nint* moduleAddress = &module;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugClassAbi(@class).GetModule((nint)moduleAddress),
            "ICorDebugClass.GetModule");
        return Volatile.Read(ref *moduleAddress);
    }

    private async ValueTask<bool> HandleUpdateModuleSymbolsAsync(
        nint module,
        nint symbolStream,
        CancellationToken cancellationToken)
    {
        if (module == 0 || symbolStream == 0)
        {
            return true;
        }

        byte[] image = ComStreamReader.ReadAll(symbolStream);
        await _sourceBreakpoints.UpdateModuleSymbolsAsync(
            module,
            image,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<bool> HandleExceptionAsync(
        nint thread,
        int eventType,
        CancellationToken cancellationToken)
    {
        if (thread == 0 || !TryGetExceptionStage(eventType, out DebugExceptionStage stage))
        {
            return true;
        }

        bool stopped = await _exceptionRaised(
            checked((int)GetThreadId(thread)),
            thread,
            stage,
            cancellationToken).ConfigureAwait(false);
        return !stopped;
    }

    private static bool TryGetExceptionStage(int eventType, out DebugExceptionStage stage)
    {
        stage = eventType switch
        {
            1 => DebugExceptionStage.Thrown,
            2 => DebugExceptionStage.UserUnhandled,
            4 => DebugExceptionStage.Unhandled,
            _ => default
        };
        return eventType is 1 or 2 or 4;
    }

    private static unsafe uint GetThreadId(nint thread)
    {
        uint threadId = 0;
        uint* threadIdAddress = &threadId;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugThreadAbi(thread).GetID((nint)threadIdAddress),
            "ICorDebugThread.GetID");
        return Volatile.Read(ref *threadIdAddress);
    }
}
