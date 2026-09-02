using Csls.Debugger.Interop;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Exposes the CoreCLR managed-debug callback contract through a NativeAOT-safe COM vtable.
/// </summary>
internal sealed class CorDebugManagedCallback : IDisposable
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
    private readonly TaskCompletionSource<int> _createProcessCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _instance;

    /// <summary>
    /// Creates one callback object with an independently reference-counted native identity.
    /// </summary>
    /// <param name="actor">The engine actor that owns callback continuation.</param>
    internal unsafe CorDebugManagedCallback(DebuggerSessionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        _actor = actor;
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

    /// <inheritdoc />
    public void Dispose()
    {
        nint instance = Interlocked.Exchange(ref _instance, 0);
        if (instance != 0)
        {
            _ = ReleaseCore(instance);
        }
    }

    private static unsafe nint CreateCallbackVtable()
    {
        nint memory = RuntimeHelpers.AllocateTypeAssociatedMemory(
            typeof(CorDebugManagedCallback),
            ICorDebugManagedCallbackAbi.VtableSlotCount * sizeof(nint));
        nint* vtable = (nint*)memory;
        vtable[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
        vtable[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        vtable[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        vtable[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)&Breakpoint;
        vtable[4] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int, int>)&StepComplete;
        vtable[5] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&Break;
        vtable[6] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int, int>)&Exception;
        vtable[7] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)&EvalComplete;
        vtable[8] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)&EvalException;
        vtable[9] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&CreateProcess;
        vtable[10] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&ExitProcess;
        vtable[11] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&CreateThread;
        vtable[12] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&ExitThread;
        vtable[13] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&LoadModule;
        vtable[14] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&UnloadModule;
        vtable[15] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&LoadClass;
        vtable[16] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&UnloadClass;
        vtable[17] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int, uint, int>)&DebuggerError;
        vtable[18] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int, nint, nint, int>)&LogMessage;
        vtable[19] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int, uint, nint, nint, int>)&LogSwitch;
        vtable[20] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&CreateAppDomain;
        vtable[21] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&ExitAppDomain;
        vtable[22] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&LoadAssembly;
        vtable[23] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&UnloadAssembly;
        vtable[24] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&ControlCTrap;
        vtable[25] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&NameChange;
        vtable[26] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)&UpdateModuleSymbols;
        vtable[27] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int, int>)&EditAndContinueRemap;
        vtable[28] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, uint, int>)&BreakpointSetError;
        return memory;
    }

    private static unsafe nint CreateCallback2Vtable()
    {
        nint memory = RuntimeHelpers.AllocateTypeAssociatedMemory(
            typeof(CorDebugManagedCallback),
            ICorDebugManagedCallback2Abi.VtableSlotCount * sizeof(nint));
        nint* vtable = (nint*)memory;
        vtable[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
        vtable[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        vtable[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        vtable[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, nint, uint, int>)&FunctionRemapOpportunity;
        vtable[4] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, nint, int>)&CreateConnection;
        vtable[5] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, int>)&ChangeConnection;
        vtable[6] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, int>)&DestroyConnection;
        vtable[7] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, uint, int, uint, int>)&Exception2;
        vtable[8] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int, uint, int>)&ExceptionUnwind;
        vtable[9] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)&FunctionRemapComplete;
        vtable[10] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)&MdaNotification;
        return memory;
    }

    private static unsafe nint CreateCallback3Vtable()
    {
        nint memory = RuntimeHelpers.AllocateTypeAssociatedMemory(
            typeof(CorDebugManagedCallback),
            ICorDebugManagedCallback3Abi.VtableSlotCount * sizeof(nint));
        nint* vtable = (nint*)memory;
        vtable[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
        vtable[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        vtable[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        vtable[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&CustomNotification;
        return memory;
    }

    private static unsafe nint CreateCallback4Vtable()
    {
        nint memory = RuntimeHelpers.AllocateTypeAssociatedMemory(
            typeof(CorDebugManagedCallback),
            ICorDebugManagedCallback4Abi.VtableSlotCount * sizeof(nint));
        nint* vtable = (nint*)memory;
        vtable[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
        vtable[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        vtable[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        vtable[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&BeforeGarbageCollection;
        vtable[4] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&AfterGarbageCollection;
        vtable[5] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, uint, int>)&DataBreakpoint;
        return memory;
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

    private static int QueueContinue(nint self, nint controller, bool createsProcess)
    {
        if (controller == 0)
        {
            return NullPointerHResult;
        }

        CorDebugManagedCallback target = GetTarget(self);
        _ = ComAbi.AddRef(controller);
        nint ownedController = controller;
        Task operation = target._actor.InvokeAsync(
            actorCancellationToken =>
            {
                _ = actorCancellationToken;
                nint currentController = Interlocked.Exchange(ref ownedController, 0);
                try
                {
                    int result = new ICorDebugControllerAbi(currentController)
                        .Continue(fIsOutOfBand: 0);
                    if (createsProcess)
                    {
                        _ = target._createProcessCompletion.TrySetResult(result);
                    }

                    CorDebugHResult.ThrowIfFailed(result, "ICorDebugController.Continue");
                    return ValueTask.CompletedTask;
                }
                finally
                {
                    _ = ComAbi.Release(currentController);
                }
            },
            CancellationToken.None);
        _ = target.ObserveOperationAsync(operation, () =>
        {
            nint currentController = Interlocked.Exchange(ref ownedController, 0);
            if (currentController != 0)
            {
                _ = ComAbi.Release(currentController);
            }
        });
        return SuccessHResult;
    }

    private async Task ObserveOperationAsync(Task operation, Action releaseUnclaimed)
    {
        try
        {
            await operation.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            releaseUnclaimed();
            _ = _createProcessCompletion.TrySetException(exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Breakpoint(nint self, nint appDomain, nint thread, nint breakpoint)
    {
        _ = thread;
        _ = breakpoint;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int StepComplete(nint self, nint appDomain, nint thread, nint stepper, int reason)
    {
        _ = thread;
        _ = stepper;
        _ = reason;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Break(nint self, nint appDomain, nint thread)
    {
        _ = thread;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Exception(nint self, nint appDomain, nint thread, int unhandled)
    {
        _ = thread;
        _ = unhandled;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EvalComplete(nint self, nint appDomain, nint thread, nint eval)
    {
        _ = thread;
        _ = eval;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EvalException(nint self, nint appDomain, nint thread, nint eval)
    {
        _ = thread;
        _ = eval;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateProcess(nint self, nint process)
    {
        return QueueContinue(self, process, createsProcess: true);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ExitProcess(nint self, nint process)
    {
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateThread(nint self, nint appDomain, nint thread)
    {
        _ = thread;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ExitThread(nint self, nint appDomain, nint thread)
    {
        _ = thread;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int LoadModule(nint self, nint appDomain, nint module)
    {
        _ = module;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int UnloadModule(nint self, nint appDomain, nint module)
    {
        _ = module;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int LoadClass(nint self, nint appDomain, nint @class)
    {
        _ = @class;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int UnloadClass(nint self, nint appDomain, nint @class)
    {
        _ = @class;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DebuggerError(nint self, nint process, int error, uint errorCode)
    {
        _ = error;
        _ = errorCode;
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int LogMessage(nint self, nint appDomain, nint thread, int level, nint category, nint message)
    {
        _ = thread;
        _ = level;
        _ = category;
        _ = message;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int LogSwitch(nint self, nint appDomain, nint thread, int level, uint reason, nint category, nint parent)
    {
        _ = thread;
        _ = level;
        _ = reason;
        _ = category;
        _ = parent;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateAppDomain(nint self, nint process, nint appDomain)
    {
        _ = appDomain;
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ExitAppDomain(nint self, nint process, nint appDomain)
    {
        _ = appDomain;
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int LoadAssembly(nint self, nint appDomain, nint assembly)
    {
        _ = assembly;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int UnloadAssembly(nint self, nint appDomain, nint assembly)
    {
        _ = assembly;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ControlCTrap(nint self, nint process)
    {
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int NameChange(nint self, nint appDomain, nint thread)
    {
        _ = thread;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int UpdateModuleSymbols(nint self, nint appDomain, nint module, nint symbolStream)
    {
        _ = module;
        _ = symbolStream;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EditAndContinueRemap(nint self, nint appDomain, nint thread, nint function, int accurate)
    {
        _ = thread;
        _ = function;
        _ = accurate;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int BreakpointSetError(nint self, nint appDomain, nint thread, nint breakpoint, uint error)
    {
        _ = thread;
        _ = breakpoint;
        _ = error;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FunctionRemapOpportunity(
        nint self,
        nint appDomain,
        nint thread,
        nint oldFunction,
        nint newFunction,
        uint oldIlOffset)
    {
        _ = thread;
        _ = oldFunction;
        _ = newFunction;
        _ = oldIlOffset;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateConnection(
        nint self,
        nint process,
        uint connectionId,
        nint connectionName)
    {
        _ = connectionId;
        _ = connectionName;
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ChangeConnection(nint self, nint process, uint connectionId)
    {
        _ = connectionId;
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DestroyConnection(nint self, nint process, uint connectionId)
    {
        _ = connectionId;
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Exception2(
        nint self,
        nint appDomain,
        nint thread,
        nint frame,
        uint offset,
        int eventType,
        uint flags)
    {
        _ = thread;
        _ = frame;
        _ = offset;
        _ = eventType;
        _ = flags;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ExceptionUnwind(
        nint self,
        nint appDomain,
        nint thread,
        int eventType,
        uint flags)
    {
        _ = thread;
        _ = eventType;
        _ = flags;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FunctionRemapComplete(
        nint self,
        nint appDomain,
        nint thread,
        nint function)
    {
        _ = thread;
        _ = function;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int MdaNotification(
        nint self,
        nint controller,
        nint thread,
        nint mda)
    {
        _ = thread;
        _ = mda;
        return QueueContinue(self, controller, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CustomNotification(nint self, nint thread, nint appDomain)
    {
        _ = thread;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int BeforeGarbageCollection(nint self, nint process)
    {
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int AfterGarbageCollection(nint self, nint process)
    {
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DataBreakpoint(
        nint self,
        nint process,
        nint thread,
        nint context,
        uint contextSize)
    {
        _ = thread;
        _ = context;
        _ = contextSize;
        return QueueContinue(self, process, createsProcess: false);
    }
}
