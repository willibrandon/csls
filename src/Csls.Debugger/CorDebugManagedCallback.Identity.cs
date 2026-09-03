using Csls.Debugger.Interop;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Exposes the CoreCLR managed-debug callback contract through a NativeAOT-safe COM vtable.
/// </summary>
internal sealed partial class CorDebugManagedCallback : IDisposable
{
    private const int SuccessHResult = 0;
    private const int NoInterfaceHResult = unchecked((int)0x80004002);
    private const int NullPointerHResult = unchecked((int)0x80004003);
    private const int InterfaceHeaderSlotCount = 2;
    private const int InterfaceCount = 4;
    private const int ContextSlot = InterfaceHeaderSlotCount * InterfaceCount;
    private static readonly int s_referenceCountOffset = (ContextSlot + 1) * nint.Size;
    private static readonly Guid s_iUnknownInterfaceId =
        new("00000000-0000-0000-C000-000000000046");
    private static readonly nint s_callbackVtable = CreateCallbackVtable();
    private static readonly nint s_callback2Vtable = CreateCallback2Vtable();
    private static readonly nint s_callback3Vtable = CreateCallback3Vtable();
    private static readonly nint s_callback4Vtable = CreateCallback4Vtable();
    private readonly DebuggerSessionActor _actor;
    private readonly SourceBreakpointManager _sourceBreakpoints;
    private readonly Func<int, CancellationToken, ValueTask> _breakpointStopped;
    private readonly Func<int, nint, int, CancellationToken, ValueTask<bool>> _stepCompleted;
    private readonly Func<int, nint, DebugExceptionStage, CancellationToken, ValueTask<bool>>
        _exceptionRaised;
    private readonly TaskCompletionSource<int> _createProcessCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exitProcessCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _instance;

    /// <summary>
    /// Creates one callback object with an independently reference-counted native identity.
    /// </summary>
    /// <param name="actor">The engine actor that owns callback continuation.</param>
    /// <param name="sourceBreakpoints">The source-breakpoint binding owner.</param>
    /// <param name="breakpointStopped">The ordered source-breakpoint stop callback.</param>
    /// <param name="stepCompleted">The ordered source-step completion callback.</param>
    /// <param name="exceptionRaised">The ordered managed-exception callback.</param>
    internal unsafe CorDebugManagedCallback(
        DebuggerSessionActor actor,
        SourceBreakpointManager sourceBreakpoints,
        Func<int, CancellationToken, ValueTask> breakpointStopped,
        Func<int, nint, int, CancellationToken, ValueTask<bool>> stepCompleted,
        Func<int, nint, DebugExceptionStage, CancellationToken, ValueTask<bool>> exceptionRaised)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(sourceBreakpoints);
        ArgumentNullException.ThrowIfNull(breakpointStopped);
        ArgumentNullException.ThrowIfNull(stepCompleted);
        ArgumentNullException.ThrowIfNull(exceptionRaised);
        _actor = actor;
        _sourceBreakpoints = sourceBreakpoints;
        _breakpointStopped = breakpointStopped;
        _stepCompleted = stepCompleted;
        _exceptionRaised = exceptionRaised;
        nuint allocationSize = checked((nuint)(s_referenceCountOffset + sizeof(int)));
        _instance = (nint)NativeMemory.AllocZeroed(allocationSize);
        if (_instance == 0)
        {
            throw new InvalidOperationException(
                "The native managed-callback allocation failed.");
        }

        var context = GCHandle.Alloc(this, GCHandleType.Normal);
        nint* instance = (nint*)_instance;
        instance[0] = s_callbackVtable;
        instance[1] = _instance;
        instance[2] = s_callback2Vtable;
        instance[3] = _instance;
        instance[4] = s_callback3Vtable;
        instance[5] = _instance;
        instance[6] = s_callback4Vtable;
        instance[7] = _instance;
        instance[ContextSlot] = GCHandle.ToIntPtr(context);
        *(int*)((byte*)_instance + s_referenceCountOffset) = 1;
    }

    /// <summary>
    /// Gets the COM interface pointer accepted by ICorDebug.SetManagedHandler.
    /// </summary>
    internal nint Pointer => Volatile.Read(ref _instance);

    /// <summary>
    /// Waits until CoreCLR reports and resumes the initial create-process stop.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait for the initial managed callback.</param>
    /// <returns>A task that completes after the runtime accepts Continue.</returns>
    internal async Task WaitForCreateProcessAsync(CancellationToken cancellationToken)
    {
        int result = await _createProcessCompletion.Task.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        CorDebugHResult.ThrowIfFailed(result, "ICorDebugController.Continue");
    }

    /// <summary>
    /// Waits until CoreCLR delivers the terminal process callback on the engine actor.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for callback delivery.</param>
    /// <returns>A task that completes after the callback relinquishes its process pointer.</returns>
    internal Task WaitForExitProcessAsync(CancellationToken cancellationToken) =>
        _exitProcessCompletion.Task.WaitAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        nint instance = Interlocked.Exchange(ref _instance, 0);
        if (instance != 0)
        {
            _ = ReleaseCore(instance);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int QueryInterface(nint self, Guid* interfaceId, nint* result)
    {
        if (interfaceId is null || result is null)
        {
            return NullPointerHResult;
        }

        nint root = Root(self);
        *result = *interfaceId == s_iUnknownInterfaceId ||
            *interfaceId == ICorDebugManagedCallbackAbi.InterfaceId
                ? root
                : *interfaceId == ICorDebugManagedCallback2Abi.InterfaceId
                    ? root + (InterfaceHeaderSlotCount * nint.Size)
                    : *interfaceId == ICorDebugManagedCallback3Abi.InterfaceId
                        ? root + (2 * InterfaceHeaderSlotCount * nint.Size)
                        : *interfaceId == ICorDebugManagedCallback4Abi.InterfaceId
                            ? root + (3 * InterfaceHeaderSlotCount * nint.Size)
                            : 0;
        if (*result == 0)
        {
            return NoInterfaceHResult;
        }

        _ = AddRefCore(self);
        return SuccessHResult;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint self) =>
        AddRefCore(self);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint self) => ReleaseCore(self);

    private static uint AddRefCore(nint self) =>
        unchecked((uint)Interlocked.Increment(ref ReferenceCount(self)));

    private static unsafe uint ReleaseCore(nint self)
    {
        int remaining = Interlocked.Decrement(ref ReferenceCount(self));
        if (remaining == 0)
        {
            nint root = Root(self);
            nint contextPointer = ((nint*)root)[ContextSlot];
            if (contextPointer != 0)
            {
                GCHandle.FromIntPtr(contextPointer).Free();
            }

            NativeMemory.Free((void*)root);
        }

        return unchecked((uint)remaining);
    }

    private static unsafe ref int ReferenceCount(nint self) =>
        ref *(int*)((byte*)Root(self) + s_referenceCountOffset);

    private static unsafe CorDebugManagedCallback GetTarget(nint self)
    {
        nint context = ((nint*)Root(self))[ContextSlot];
        return (CorDebugManagedCallback)GCHandle.FromIntPtr(context).Target!;
    }

    private static unsafe nint Root(nint self) => ((nint*)self)[1];

}
