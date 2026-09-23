using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Initializes and attaches an owned debugger interface on the session actor.
/// </summary>
internal static class CorDebugRuntimeActivation
{
    /// <summary>
    /// Consumes a dbgshim interface and transfers the initialized debugger and process references.
    /// </summary>
    /// <param name="debugger">The owned IUnknown interface consumed on success and failure.</param>
    /// <param name="processId">The selected target process.</param>
    /// <param name="managedCallback">The managed callback installed before attachment.</param>
    /// <param name="sourceBreakpoints">The manager configured before module callbacks run.</param>
    /// <returns>The owned debugger and process references.</returns>
    internal static unsafe CorDebugActivationResult Attach(
        nint debugger,
        uint processId,
        CorDebugManagedCallback managedCallback,
        SourceBreakpointManager sourceBreakpoints)
    {
        nint corDebug = 0;
        nint attachedProcess = 0;
        bool initialized = false;
        try
        {
            ArgumentOutOfRangeException.ThrowIfZero(debugger);
            corDebug = ComAbi.QueryInterface(debugger, ICorDebugAbi.InterfaceId);
            var api = new ICorDebugAbi(corDebug);
            CorDebugHResult.ThrowIfFailed(api.Initialize(), "ICorDebug.Initialize");
            initialized = true;
            CorDebugHResult.ThrowIfFailed(
                api.SetManagedHandler(managedCallback.Pointer), "ICorDebug.SetManagedHandler");
            nint nativeProcess = 0;
            nint* processAddress = &nativeProcess;
            int attachResult = api.DebugActiveProcess(processId, win32Attach: 0, (nint)processAddress);
            attachedProcess = Volatile.Read(ref *processAddress);
            managedCallback.ThrowIfRuntimeFailed();
            CorDebugHResult.ThrowIfFailed(attachResult, "ICorDebug.DebugActiveProcess");
            if (attachedProcess == 0)
            {
                throw new InvalidOperationException(
                    "ICorDebug.DebugActiveProcess succeeded without returning a process.");
            }

            sourceBreakpoints.SetRuntimeVersion(CorDebugRuntimeVersionReader.TryRead(attachedProcess));
            var result = new CorDebugActivationResult(corDebug, attachedProcess);
            corDebug = 0;
            attachedProcess = 0;
            return result;
        }
        finally
        {
            if (attachedProcess != 0)
            {
                if (managedCallback.RuntimeFailure is null)
                {
                    _ = new ICorDebugControllerAbi(attachedProcess).Detach();
                }

                _ = ComAbi.Release(attachedProcess);
            }

            if (corDebug != 0)
            {
                if (initialized && managedCallback.RuntimeFailure is null)
                {
                    _ = new ICorDebugAbi(corDebug).Terminate();
                }

                _ = ComAbi.Release(corDebug);
            }

            if (debugger != 0)
            {
                _ = ComAbi.Release(debugger);
            }
        }
    }
}
