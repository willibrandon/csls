using Csls.Debugger.Interop;
using System.Runtime.CompilerServices;

namespace Csls.Debugger;

/// <summary>
/// Constructs NativeAOT-safe COM vtables for managed debugger callbacks.
/// </summary>
internal sealed partial class CorDebugManagedCallback
{
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

}
